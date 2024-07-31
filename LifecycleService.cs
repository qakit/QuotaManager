using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GridQuota;

public class LifecycleService(
	IOptionsMonitor<AppConfig> _configOptions,
	IWorkerRegistry _balancer,
	IHttpClientFactory _clientFactory,
	ILogger<LifecycleService> _logger,
	SessionHandler _sessionHandler
) : IHostedService
{
	private readonly CancellationTokenSource _watchdogsCts = new();
	private readonly IList<Task> _watchdogs = [];
	private readonly List<IDisposable?> _disposables = [];

	private readonly AutoResetEvent _triggerReloadConfig = new(false);

	public async Task ExpiredSessionsWatchdog(CancellationToken cancel)
	{
		while (!cancel.IsCancellationRequested)
		{
			try
			{
				await _sessionHandler.CleanupExpiredSessions();
				await Task.Delay(1000, cancel);
			}
			catch (TaskCanceledException) { break; }
		}
	}

	public async Task HostReconfigWatchdog(CancellationToken cancel)
	{
		while (!cancel.IsCancellationRequested)
		{
			try
			{
				var awaitInterval = _configOptions.CurrentValue.CheckAliveInterval;

				await SyncConfig(cancel);
				await Task.WhenAny(Task.Delay(awaitInterval, cancel), _triggerReloadConfig.AsTask());
				_triggerReloadConfig.Reset();
			}
			catch (TaskCanceledException) { break; }
		}
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Connecting hosts");
		_logger.LogDebug("Current config: {Config}", JsonSerializer.Serialize(_configOptions.CurrentValue));

		await SyncConfig(cancellationToken);
		_watchdogs.Add(
			ExpiredSessionsWatchdog(_watchdogsCts.Token)
		);
		_watchdogs.Add(
			HostReconfigWatchdog(_watchdogsCts.Token)
		);

		_disposables.Add(
			_configOptions.OnChange(_ =>
			{
				_logger.LogInformation($"Config has changed, reapplying");
				_triggerReloadConfig.Set();
			})
		);
	}

	private async Task SyncConfig(CancellationToken _)
	{
		var runningHosts = _balancer.GetConfig().ToDictionary(c => new Uri(c.HostUri), c => c);
		var newConfig = _configOptions.CurrentValue;
		var newHosts = newConfig.Hosts.ToDictionary(c => new Uri(c.HostUri), c => c);

		var hostsStatus = await CheckAliveStatus(from h in newConfig.Hosts select new Uri(h.HostUri));
		Predicate<Uri> isAlive = uri =>
			hostsStatus.TryGetValue(uri, out var x) && x
			&& newHosts.TryGetValue(uri, out var config) && config.Limit > 0;

		var toBeRemoved =
			(from host in runningHosts.Keys
			 where !newHosts.ContainsKey(host) || !isAlive(host)
			 select host).ToArray();
		var toBeStarted =
			(from host in newHosts.Keys
			 where (!runningHosts.ContainsKey(host) || !AreConfigsEqual(newHosts[host], runningHosts[host])) && isAlive(host)
			 select host).ToArray();

		if (toBeRemoved.Length != 0 || toBeStarted.Length != 0)
		{
			_logger.LogInformation("Detected changes in configuration or availability of the hosts");
		}

		await Task.WhenAll(
			from host in toBeRemoved
			select _balancer.DeleteHost(host)
			);
		await Task.WhenAll(
			from host in toBeStarted
			select _balancer.AddHost(newHosts[host])
			);

		// TODO update status monitor queue
		// if (hostsStatus.Any(status => status.Value == false))
		// {
		// 	var deadHosts = from h in newConfig.Hosts where !isAlive(h.HostUri) select h.HostUri;
		// 	_logger.LogWarning("Hosts not available: {0}", string.Join(", ", deadHosts.ToArray()));
		// }
	}

	private static bool AreConfigsEqual(HostConfig config1, HostConfig config2)
	{
		var json1 = JsonSerializer.Serialize(config1);
		var json2 = JsonSerializer.Serialize(config2);
		return json1 == json2;
	}

	private async Task<IDictionary<Uri, bool>> CheckAliveStatus(IEnumerable<Uri> hosts)
	{
		static async Task<bool> Kick(Uri endpoint, HttpClient client, CancellationToken cancel)
		{
			try
			{
				return (await client.GetAsync(endpoint, cancel)).IsSuccessStatusCode;
			}
			catch (Exception)
			{
				return false;
			}
		}

		using var httpCli = _clientFactory.CreateClient("checkalive");
		using var cts = new CancellationTokenSource(2000);

		var result = await Task.WhenAll(
			from host in hosts
			let statusUri = new Uri(host, "status")
			select Kick(statusUri, httpCli, cts.Token).ContinueWith(task => new { host, alive = task.Result })
		);
		var hostsStatus = result.ToDictionary(v => v.host, v => v.alive);
		return hostsStatus;
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		_logger.LogInformation("Stopping services");

		_disposables.ForEach(d => d?.Dispose());
		_disposables.Clear();
		
		_watchdogsCts.Cancel();

		var configs = _balancer.RunningHosts;
		await Task.WhenAll(
			configs.Select(_balancer.DeleteHost)
			.Concat(new[] { _sessionHandler.TerminateAllSessions(cancellationToken) })
			.Concat(_watchdogs)
			);
		_watchdogs.Clear();
	}
}
