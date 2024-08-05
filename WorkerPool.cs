using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SeleniumSwissKnife;

public record WorkerStatsPayload(int Available, int WaitingForJob);


public class WorkerPool(ILogger<WorkerPool> _logger, QuotaManager quotas) : IWorkerPool, IWorkerRegistry
{
    private class WorkerData
    {
        private readonly TaskCompletionSource _workerStop = new();

        public required WorkerInfo Info { get; init; }
        public bool IsRunning { get; private set; } = true;
        public void Stop() { IsRunning = false; _workerStop.SetResult(); }

        public async Task AwaitCompletion(CancellationToken cancel)
        {
            if (IsRunning) await _workerStop.Task;
        }
    }

    private record HostData(Uri HostUri, HostConfig Config, List<WorkerData> Workers);

    const int MaxWorkerPerHost = 10000;

    private readonly Channel<WorkerData> _workerQueue = Channel.CreateUnbounded<WorkerData>();

    private readonly ConcurrentDictionary<Uri, HostData> _runningHosts = new();

    #region IWorkerPool

    /// <summary>
    /// Semaphore to ensure only one thread is reading from the queue at a time.
    /// </summary>
    private readonly SemaphoreSlim _queueReadSemaphore = new(1, 1);

    public async Task<JobToken> GetNext(Request req, CancellationToken cancel)
    {
        await quotas.AwaitQuota(req.UserId, cancel);

        var reader = _workerQueue.Reader;

        try
        {
            await _queueReadSemaphore.WaitAsync(cancel);
            while (true)
            {
                var worker = await reader.ReadAsync(cancel);
                if (!worker.IsRunning) continue;

                return new JobToken(worker.Info, req, job => ReleaseJob(job, worker));
            }
        }
        finally
        {
            _queueReadSemaphore.Release();
        }
    }

    private void ReleaseJob(JobToken job, WorkerData worker)
    {
        quotas.ReleaseQuota(job.Request.UserId);
        if (worker.IsRunning) _workerQueue.Writer.TryWrite(worker);
    }

    #endregion

    #region IWorkerRegistry

    public IEnumerable<Uri> RunningHosts => [.. _runningHosts.Keys];

    public async Task<bool> AddHost(HostConfig config)
    {
        var hostUri = config.HostUri;
        await DeleteHost(hostUri);
        _logger.LogInformation("Connecting to host/grid '{hostUri}'", hostUri);

        var writer = _workerQueue.Writer;

        // start or updates tasks for a specific host
        var workerCount = Math.Min(Math.Max(1, config.Limit), MaxWorkerPerHost);
        var workerInfo = new WorkerInfo(hostUri);
        var workers = Enumerable.Range(0, workerCount).Select(_ => new WorkerData { Info = workerInfo }).ToList();

        // TODO sync access
        if (_runningHosts.TryAdd(hostUri, new HostData(hostUri, config, workers)))
        {
            foreach (var worker in workers)
            {
                await writer.WriteAsync(worker);
            }
        }
        return true;
    }
    public async Task<bool> DeleteHost(Uri host)
    {
        if (_runningHosts.TryRemove(host, out var hostInfo))
        {
            _logger.LogInformation("Disconnecting from host/grid '{host}'", host);
            hostInfo.Workers.ForEach(w => w.Stop()); // that simply sets a flag for worker not to be re-added to queue.
            await Task.WhenAll(hostInfo.Workers.Select(w => w.AwaitCompletion(CancellationToken.None)));

            return true;
        }
        return false;
    }

    public HostConfig[] GetConfig() => _runningHosts.Values.Select(h => h.Config).ToArray();

    #endregion


    public WorkerStatsPayload GetStats()
    {
        var workerPoolSize = _runningHosts.Values.Sum(h => h.Workers.Count);
        var workerQueueSize = _workerQueue.Reader.Count;

        return new WorkerStatsPayload(workerPoolSize, workerQueueSize);
    }
}