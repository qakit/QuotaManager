using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxyKit;

namespace GridQuota;

public record SessionData(Uri Upstream, Uri HostUri, ResourceToken<Uri> Worker, TimeSpan SessionTimeout)
{
	public UpstreamHost UpstreamHost { get; } = new UpstreamHost(Upstream.AbsoluteUri);
}

public class SessionHandler(
	ILogger<SessionHandler> _logger,
	IWorkerPool<Uri> balancer,
	IHttpClientFactory _factory,
	IOptions<AppConfig> _appConfig) : IProxyHandler
{
	/// <summary>
	/// How much time does wait for remote session to be deleted after client dropped connection or session expired.
	/// </summary>
	private const int DeleteSessionTimeoutMs = 4000;

	private readonly IWorkerPool<Uri> _balancer = balancer;

	private readonly IDictionary<string, SessionData> _sessions = new ConcurrentDictionary<string, SessionData>();
	private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionsExpires = new();
	private readonly ConcurrentDictionary<Uri, DateTimeOffset> _lastHostAccess = new();
	private readonly object _expiryCleanupLock = new();

	private static async Task<string> ReadRequestBody(HttpContext context)
	{
		context.Request.EnableBuffering();
		using var reader = new StreamReader(
			context.Request.Body,
			encoding: Encoding.UTF8,
			detectEncodingFromByteOrderMarks: false,
			bufferSize: 10000,
			leaveOpen: true);
		var body = await reader.ReadToEndAsync();
		// Reset the request body stream position so the next middleware can read it
		context.Request.Body.Position = 0;
		return body;
	}

	private async Task RemoteDeleteSession(string sessionId, Uri sessionEndpoint, CancellationToken cancel)
	{
		using var client = _factory.CreateClient("delete");
		var deleteSession = new Uri(sessionEndpoint, $"session/{sessionId}");

		_logger.LogInformation("Sending DELETE request! {0}", deleteSession);

		try
		{
			var response = await client.DeleteAsync(deleteSession, cancel);
			_logger.LogDebug("Completed DELETE request with {0} code.", response.StatusCode);
		}
		catch (Exception e)
		{
			_logger.LogError("sending DELETE to a '{0}' failed: {1}", deleteSession, e.Message);
		}
	}

	public async Task<HttpResponseMessage> HandleProxyRequest(HttpContext context)
	{
		if (context.Request.Path == "" && context.Request.Method == "POST")
		{
			_logger.LogDebug("Initiating new session (POST /session) from {RemoteIp}", context.Connection.RemoteIpAddress);
			return await ProcessSessionRequest(context);
		}

		// Otherwise just forward request
		var sessionId = ParseSessionId(context.Request.Path);
		if (!_sessions.TryGetValue(sessionId, out var sessionData))
		{
			_logger.LogWarning("Failed to find session '{sessionId}', unknown request", sessionId);
			return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
		}

		_logger.LogDebug("Forwarding request to {Host}", sessionData.Upstream);
		var response = await context
			.ForwardTo(sessionData.Upstream)
			.AddXForwardedHeaders()
			.Send();

		if (context.RequestAborted.IsCancellationRequested)
		{
			ReleaseSession(sessionId);
			_logger.LogWarning("Session cancelled (client disconnected) {sessionId}", sessionId);

			using var cts = new CancellationTokenSource(DeleteSessionTimeoutMs);
			await RemoteDeleteSession(sessionId, sessionData.Upstream, cts.Token);
		}
		else if (context.Request.Method == "DELETE")
		{
			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError("DELETE '{Upstream}' query for {SessionId} session failed", sessionData.Upstream, sessionId);
			}

			ReleaseSession(sessionId);
			_logger.LogInformation("Closed session: {SessionId} on host '{Upstream}'", sessionId, sessionData.Upstream);
		}
		else
		{
			Touch(sessionId, sessionData.HostUri);
		}
		return response;
	}

	private void ReleaseSession(string sessionId)
	{
		if (_sessions.Remove(sessionId, out var sessionData))
		{
			sessionData.Worker.Dispose();
			_sessionsExpires.Remove(sessionId, out var _);
			// TODO consider _registry.ReleaseHost(sessionData.Host)
		}
	}

	private void Touch(string sessionId, Uri host)
	{
		if (_sessions.TryGetValue(sessionId, out var sessionData))
		{
			var now = DateTimeOffset.UtcNow;
			var expireTime = now + sessionData.SessionTimeout;

			lock (_expiryCleanupLock)
			{
				_sessionsExpires.AddOrUpdate(sessionId, expireTime, (_, _1) => expireTime);
			}
			_lastHostAccess.AddOrUpdate(host, now, (_, _1) => now);
		}
	}

	public async Task CleanupExpiredSessions()
	{
		var expired = new List<Tuple<string, Uri, DateTimeOffset>>();
		lock (_expiryCleanupLock)
		{
			var now = DateTimeOffset.UtcNow;
			var expiredSessions = (from pair in _sessionsExpires
								   where now > pair.Value
								   select pair.Key
				).ToHashSet();
			if (expiredSessions.Count == 0) return;

			expired = (from pair in _sessions
					   let sessionId = pair.Key
					   where expiredSessions.Contains(sessionId)
					   select Tuple.Create(sessionId, pair.Value.Upstream, _sessionsExpires[sessionId])
				).ToList();
			expiredSessions.ToList().ForEach(sessionId =>
				_sessionsExpires.TryRemove(sessionId, out var _)
			);
			if (expired.Count == 0) return;
		}

		expired.ForEach(i =>
		{
			var (sessionId, _, expires) = i;
			ReleaseSession(sessionId);
			_logger.LogWarning("Removing expired session {sessionId}, expired {expires}", sessionId, expires);
		});

		using var cts = new CancellationTokenSource(DeleteSessionTimeoutMs);
		await Task.WhenAll(from i in expired select RemoteDeleteSession(i.Item1, i.Item2, cts.Token));
	}
	public Task TerminateAllSessions(CancellationToken cancel)
	{
		var sessions = (from pair in _sessions
						let sessionId = pair.Key
						select new { sessionId, endpoint = pair.Value.Upstream }
			).ToList();
		if (sessions.Count == 0) return Task.CompletedTask;

		sessions.ForEach(i => ReleaseSession(i.sessionId));
		return Task.WhenAll(from i in sessions select RemoteDeleteSession(i.sessionId, i.endpoint, cancel));
	}

	// Gets the list of hosts that router successfully communicated with after specified moment of time
	public IEnumerable<Uri> GetAliveHosts(DateTimeOffset when)
	{
		return (
			from pair in _lastHostAccess
			where pair.Value > when
			select pair.Key).ToList();
	}

	private async Task<HttpResponseMessage> ProcessSessionRequest(HttpContext context)
	{
		var retriesLeft = _appConfig.Value.SessionRetryCount;
		var retryTimeout = _appConfig.Value.SessionRetryTimeout;

		var userId = Authenticate(context);

		var body = await ReadRequestBody(context);
		_logger.LogDebug("Request body: {Body}", body);

		if (string.IsNullOrWhiteSpace(body))
		{
			_logger.LogWarning("Session request with empty body");
		}

		var caps = new Caps();

		do
		{
			var workerToken = await _balancer.GetNext(new Request(caps, userId));
			var host = workerToken.Resource.AbsoluteUri;
			var sessionEndpoint = new Uri(new Uri(host.TrimEnd('/') + "/"), "session");

			var initResponse = await context
				.ForwardTo(sessionEndpoint)
				.AddXForwardedHeaders()
				.Send();
			if (initResponse.IsSuccessStatusCode)
			{
				var responseBody = await initResponse.Content.ReadAsStringAsync();
				var seleniumResponse = JsonDocument.Parse(responseBody);
				var sessionId = seleniumResponse.RootElement.GetProperty("value").GetProperty("sessionId")
					.GetString() ?? "";

				_logger.LogInformation("New session for {Remote} user {User}: {SessionId} on host '{WorkerHost}'", context.Connection.RemoteIpAddress, userId, sessionId, workerToken.Resource);

				var updatedBody = FixupInitResponse(responseBody, context.Connection);
				if (updatedBody != responseBody)
				{
					initResponse.Content = new StringContent(updatedBody, Encoding.UTF8, "application/json");
				}

				_sessions.Add(sessionId, new SessionData(sessionEndpoint, workerToken.Resource, workerToken, _appConfig.Value.SessionTimeout));
				Touch(sessionId, workerToken.Resource);
				return initResponse;
			}
			else if (--retriesLeft <= 0)
			{
				_logger.LogError("Failed to process /session request after 3 retries");
				return initResponse;
			}
			else
			{
				_logger.LogWarning("Failed to get response from {Host}, retrying in 10s", workerToken.Resource);
				workerToken.Dispose();
				// TODO host seems to become inactive, consider _registry.ReleaseHost(worker);
				await Task.Delay(retryTimeout);
			}
		} while (true);
	}


	/// <summary>
	/// Ensures user had provided valid credentials. Returns 0 if user is not authenticated or positive integer if user is authenticated.
	/// </summary>
	/// <param name="context"></param>
	/// <param name="users"></param>
	/// <returns></returns>
	/// <exception cref="NotImplementedException"></exception>
	private int Authenticate(HttpContext context)
	{
		string? authHeader = context.Request.Headers["Authorization"];

		// Check if the header is present and starts with "Basic"
		if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}

		// Get the Base64 encoded part (after "Basic ")
		string base64Credentials = authHeader.Substring("Basic ".Length).Trim();

		// Decode the Base64 string
		string credentials = Encoding.UTF8.GetString(Convert.FromBase64String(base64Credentials));

		// Split the credentials into username and password
		int separatorIndex = credentials.IndexOf(':');
		if (separatorIndex < 0)
		{
			_logger.LogWarning("Failed to parse credentials '{credentials}' from '{authHeader}'", credentials, authHeader);
			return 0;
		}

		string username = credentials.Substring(0, separatorIndex);
		string password = credentials.Substring(separatorIndex + 1);

		var users = _appConfig.Value.Users;
		var userId = users.FindIndex(u => u.Name == username);

		if (userId <= 0)
		{
			_logger.LogWarning("User '{user}' not found", username);
			return 0;
		}
		if (users[userId].Secret != password)
		{
			_logger.LogWarning("Wrong name or password '{user}'", username);
			return 0;
		}
		return userId;
	}

	/// <summary>
	/// Updates JSON payload and replaces websocket URL passed in value/caps/se:cdp string to a proxy URL
	/// </summary>
	/// <param name="initResponse"></param>
	/// <param name="sessionId"></param>
	private string FixupInitResponse(string responseBody, ConnectionInfo connection)
	{
		const string lookupString = "\"se:cdp\": \"";
		var start = responseBody.IndexOf(lookupString);
		if (start < 0) return responseBody;

		var endIndex = responseBody.IndexOf("\"", start + lookupString.Length);
		if (endIndex < 0) return responseBody;

		var cdpUrlEscaped = responseBody.Substring(start + lookupString.Length, endIndex - start - lookupString.Length);
		var cdpUrl = cdpUrlEscaped.Replace("\\u002f", "\u002f");
		var cdpUri = new Uri(cdpUrl);
		var newUrl = "ws://" + connection.LocalIpAddress + ":" + connection.LocalPort + cdpUri.PathAndQuery;

		_logger.LogDebug("Replacing {OldValue} with {NewValue}", cdpUrl, newUrl);

		return responseBody.Substring(0, start) + lookupString + newUrl.Replace("\u002f", "\\u002f") + "\"" + responseBody.Substring(endIndex + 1);
	}

	private static string ParseSessionId(in string requestPath)
	{
		var segments = requestPath.Split('/');
		return segments.Length > 1 ? segments[1] : "#####";
	}
}