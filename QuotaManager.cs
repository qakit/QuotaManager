using GridQuota;
using Microsoft.Extensions.Options;

namespace SeleniumSwissKnife;

public record UserStatsPayload(string Name, int ActiveSessions, int MaxSessions, int WaitingInQueue);
public record QuotaStatsPayload(UserStatsPayload[] Users);

public class QuotaManager(IOptions<AppConfig> config)
{
    /// <summary>
    /// Given a number or resources, this class provides a way to acquire and release them in such a way quotas are not exceeded.
    /// </summary>
    private class QuotaWatcher(int maxResources) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(maxResources, maxResources);
        private int _queueLength = 0;

        public async Task Acquire(CancellationToken cancel)
        {
            Interlocked.Increment(ref _queueLength);
            await _gate.WaitAsync(cancel);
            Interlocked.Decrement(ref _queueLength);
        }

        public void Release() => _gate.Release();

        public int Quota => maxResources;

        public int Waiting => _queueLength;

        public int Used => maxResources - _gate.CurrentCount;

        public void Dispose() => _gate.Dispose();
    }

    private readonly QuotaWatcher[] _userQuotaWatcher =
        config.Value.Users.Select(u => new QuotaWatcher(u.MaxSessions)).ToArray();

    public QuotaStatsPayload GetStats()
    {
        var stats = new QuotaStatsPayload(
            _userQuotaWatcher.Select((u, i) => new UserStatsPayload(config.Value.Users[i].Name, u.Used, u.Quota, u.Waiting)).ToArray()
        );
        return stats;
    }

    public async Task AwaitQuota(int userId, CancellationToken cancel)
    {
        var userQuota = _userQuotaWatcher[userId];
        await userQuota.Acquire(cancel);
    }

    public void ReleaseQuota(int userId)
    {
        _userQuotaWatcher[userId].Release();
    }
}
