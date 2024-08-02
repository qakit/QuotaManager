using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace GridQuota;

public record HostData(Uri HostUri, HostConfig Config, CancellationTokenSource Cts, Task[] Runners);
public class UserStats(int maxSessions)
{
	private int _sessions = 0;

	public int Sessions => _sessions;
	public int MaxSessions => maxSessions;

	public void AddSession() => Interlocked.Increment(ref _sessions);
	public void DecSession() => Interlocked.Decrement(ref _sessions);
}

public class QuotaManager(ILogger<QuotaManager> _logger, IOptions<AppConfig> _appConfig) : IWorkerPool<Uri>, IWorkerRegistry
{
	const int MaxWorkerPerHost = 10000;

	private readonly UserStats[] _userStats =
		_appConfig.Value.Users.Select(u => new UserStats(u.MaxSessions)).ToArray();

	/// <summary>
	/// Represents a single unit of work.
	/// </summary>
	private class Ticket<TJob,TResource>(TJob job)
	{
		private readonly TaskCompletionSource<ResourceToken<TResource>> _workerAwaitToken = new();

		public TJob Job { get; private set; } = job;
		public Task<ResourceToken<TResource>> AwaitResourceToken() => _workerAwaitToken.Task;
		public void ProcessWith(ResourceToken<TResource> worker) => _workerAwaitToken.SetResult(worker);
	}

	private readonly Channel<Ticket<Request, Uri>> _queue = Channel.CreateUnbounded<Ticket<Request, Uri>>();
	private readonly ConcurrentDictionary<Uri, HostData> _runningHosts = new();

	#region IWorkerPool

	public async Task<ResourceToken<Uri>> GetNext(Request req)
	{
		var ticket = new Ticket<Request, Uri>(req);
		await _queue.Writer.WriteAsync(ticket);

		return await ticket.AwaitResourceToken();
	}

	#endregion

	#region IWorkerRegistry

	public IEnumerable<Uri> RunningHosts => [.. _runningHosts.Keys];

	public async Task<bool> AddHost(HostConfig config)
	{
		var hostUri = new Uri(config.HostUri);
		await DeleteHost(hostUri);
		_logger.LogInformation("Connecting to host/grid '{hostUri}'", hostUri);

		// start or updates tasks for a specific host
		var cts = new CancellationTokenSource();
		var hostInfo = new HostData(hostUri, config, cts, StartHost(config, cts.Token));

		// TODO sync access
		_runningHosts.TryAdd(hostUri, hostInfo);
		return true;
	}
	public async Task<bool> DeleteHost(Uri host)
	{
		if (_runningHosts.TryRemove(host, out var hostInfo))
		{
			_logger.LogInformation("Disconnecting from host/grid '{host}'", host);
			hostInfo.Cts.Cancel();

			var timeout = Task.Delay(_appConfig.Value.StopHostTimeout);
			var timeoutExpired = await Task.WhenAny(
				timeout,
				Task.WhenAll(hostInfo.Runners)) == timeout;

			if (timeoutExpired)
			{
				_logger.LogError("Failed to stop '{host}' gracefully", host);
				// TODO force stopping agent
			}
			else
			{
				_logger.LogInformation("Stopped host '{host}'", host);
			}
			return true;
		}
		return false;
	}

	public HostConfig[] GetConfig() => _runningHosts.Values.Select(h => h.Config).ToArray();

	#endregion

	private Task[] StartHost(HostConfig config, CancellationToken cancel)
	{
		var hostUri = new Uri(config.HostUri);
		var limit = Math.Min(Math.Max(1, config.Limit), MaxWorkerPerHost);

		var processingPool = new Task[limit];
		for (int i = 0; i < limit; i++)
		{
			processingPool[i] = CreateHostWorker(hostUri, cancel);
		}
		return processingPool;
	}

	private async Task CreateHostWorker(Uri hostUri, CancellationToken cancel)
	{
		var inputQueue = _queue.Reader;
		try
		{
			while (!cancel.IsCancellationRequested)
			{
				var canRead = await inputQueue.WaitToReadAsync(cancel);
				if (canRead && inputQueue.TryRead(out var ticket))
				{
					var processingComplete = new TaskCompletionSource<bool>();
					var resourceToken = new ResourceToken<Uri>(hostUri, () => processingComplete.SetResult(true));
					ticket.ProcessWith(resourceToken);

					// processing is started right away
					_userStats[ticket.Job.UserId].AddSession();
					await processingComplete.Task;
					_userStats[ticket.Job.UserId].DecSession();
				}
			}
		}
		catch (TaskCanceledException)
		{
			_logger.LogWarning("Worker for '{Host}' cancelled", hostUri);
		}
	}
}