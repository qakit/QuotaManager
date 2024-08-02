using Microsoft.Extensions.Options;

namespace GridQuota;

public record UserStatsPayload(string Name, int ActiveSessions, int MaxSessions);

public record ManagerStatsPayload(UserStatsPayload[] Users
// , int QueueLength, int RunningHosts, Uri[] Hosts
);

public class QuotaManager(IOptions<AppConfig> config)
{
	private readonly QuotaWatcher[] _userQuotaWatcher =
		config.Value.Users.Select(u => new QuotaWatcher(u.MaxSessions)).ToArray();

	public ManagerStatsPayload GetStats()
	{
		var users = _userQuotaWatcher.Select((u, i) => new UserStatsPayload(config.Value.Users[i].Name, u.Used, u.Quota)).ToArray();

		var stats = new ManagerStatsPayload(
			users
			// _queue.Reader.Count,
			// _runningHosts.Count,
			// _runningHosts.Keys.ToArray()
		);
		return stats;
	}

	public async Task AwaitQuota(int userId, CancellationToken cancel)
	{
		await _userQuotaWatcher[userId].Acquire(cancel);
	}

	public void ReleaseQuota(int userId)
	{
		_userQuotaWatcher[userId].Release();
	}
}
