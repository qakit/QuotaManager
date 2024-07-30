using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ProxyKit;

namespace GridQuota;

public class SessionHandler(ILogger<SessionHandler> _logger, IWorkerPool balancer, IHttpClientFactory _factory, IOptions<AppConfig> _appConfig) : IProxyHandler
{
    private const int DeleteSessionTimeoutMs = 4000;

    private record SessionData(Uri Upstream, Uri HostUri, Worker Worker, TimeSpan SessionTimeout)
    {
        public UpstreamHost UpstreamHost { get; } = new UpstreamHost(Upstream.AbsoluteUri);
    }

    private readonly IWorkerPool _balancer = balancer;

    private readonly IDictionary<string, SessionData> _sessions = new ConcurrentDictionary<string, SessionData>();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionsExpires = new();
    private readonly ConcurrentDictionary<Uri, DateTimeOffset> _lastHostAccess = new();
    private readonly object _expiryCleanupLock = new object();

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
        _logger.LogDebug($"New proxy '{context.Request.Method}' request '{context.Request.Path}'");

        if (context.Request.Path == "" && context.Request.Method == "POST")
        {
            _logger.LogDebug($"Initiating new session");
            return await ProcessSessionRequest(context);
        }

        // Otherwise just forward request
        var sessionId = ParseSessionId(context.Request.Path);
        if (!_sessions.TryGetValue(sessionId, out var sessionData))
        {
            _logger.LogDebug($"Failed to find session '{sessionId}', unknown request");
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        _logger.LogDebug($"Forwarding request to {sessionData.Upstream}");
        var response = await context
            .ForwardTo(sessionData.Upstream)
            .AddXForwardedHeaders()
            .Send();

        // TODO the session might have been deleted by that time, do nothing?
        if (context.RequestAborted.IsCancellationRequested)
        {
            ReleaseSession(sessionId);
            _logger.LogError($"Session cancelled! {sessionId}");

            using var cts = new CancellationTokenSource(DeleteSessionTimeoutMs);
            await RemoteDeleteSession(sessionId, sessionData.Upstream, cts.Token);
        }
        else if (context.Request.Method == "DELETE")
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("DELETE '{0}' query for {1} session failed", sessionData.Upstream, sessionId);
            }

            ReleaseSession(sessionId);
            _logger.LogInformation($"Closed session: {sessionId} on host '{sessionData.Upstream}'");
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
            if (!expiredSessions.Any()) return;

            expired = (from pair in _sessions
                       let sessionId = pair.Key
                       where expiredSessions.Contains(sessionId)
                       select Tuple.Create(sessionId, pair.Value.Upstream, _sessionsExpires[sessionId])
                ).ToList();
            expiredSessions.ToList().ForEach(sessionId =>
                _sessionsExpires.TryRemove(sessionId, out var _)
            );
            if (!expired.Any()) return;
        }

        expired.ForEach(i =>
        {
            var (sessionId, _, expires) = i;
            ReleaseSession(sessionId);
            _logger.LogWarning($"Removing expired session {sessionId}, expired {expires}");
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
        if (!sessions.Any()) return Task.CompletedTask;

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

        var body = await ReadRequestBody(context);
        _logger.LogDebug("Request: {0} from {0}", body, context.Connection.LocalIpAddress);

        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogDebug("Session request with empty body");
        }

        _logger.LogInformation($"New session request from {context.Connection.RemoteIpAddress}");
        var sessionTimeout = TimeSpan.FromSeconds(60); // TODO: make configurable
		var caps = new Caps();

        do
        {
            var worker = await _balancer.GetNext(new Request(caps));
            var sessionEndpoint = new Uri(new Uri(worker.Host.AbsoluteUri.TrimEnd('/') + "/"), "session");

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

                _logger.LogInformation("New session: {SessionId} on host '{WorkerHost}'", sessionId, worker.Host);

				var updatedBody = fixupInitResponse(responseBody, context.Connection);
				if(updatedBody != responseBody)
				{
					initResponse.Content = new StringContent(updatedBody, Encoding.UTF8, "application/json");
				}

                _sessions.Add(sessionId, new SessionData(sessionEndpoint, worker.Host, worker, sessionTimeout));
                Touch(sessionId, worker.Host);
                return initResponse;
            }
            else if (--retriesLeft <= 0)
            {
                _logger.LogError("Failed to process /session request after 3 retries");
                return initResponse;
            }
            else
            {
                _logger.LogWarning("Failed to get response from {0}, retrying in 10s", worker.Host);
                worker.Dispose();
                // TODO consider _registry.ReleaseHost(worker);
                await Task.Delay(retryTimeout);
            }
        } while (true);
    }

    /// <summary>
    /// Updates JSON payload and replaces websocket URL passed in value/caps/se:cdp string to a proxy URL
    /// </summary>
    /// <param name="initResponse"></param>
    /// <param name="sessionId"></param>
    private string fixupInitResponse(string responseBody, ConnectionInfo connection)
    {
		const string lookupString = "\"se:cdp\": \"";
		var start = responseBody.IndexOf(lookupString);
		if (start < 0) return responseBody;

		var endIndex = responseBody.IndexOf("\"", start + lookupString.Length);
		if (endIndex < 0) return responseBody;

		var cdpUrlEscaped = responseBody.Substring(start + lookupString.Length, endIndex - start - lookupString.Length);
		var cdpUrl = cdpUrlEscaped.Replace("\\u002f", "\u002f");
		var cdpUri = new Uri(cdpUrl);
		cdpUrl = "ws://" + connection.LocalIpAddress + ":" + connection.LocalPort + cdpUri.PathAndQuery;

		_logger.LogDebug("Replacing {0} with {1}", cdpUrlEscaped, cdpUrl);

		return responseBody.Substring(0, start) + lookupString + cdpUrl.Replace("\u002f", "\\u002f") + "\"" + responseBody.Substring(endIndex + 1);
    }

    private static string ParseSessionId(in string requestPath)
    {
        var segments = requestPath.Split('/');
        return segments.Length > 1 ? segments[1] : "#####";
    }
}