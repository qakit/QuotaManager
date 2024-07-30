using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace GridQuota;

public class QuotaManager(ILogger<QuotaManager> logger, IHttpClientFactory clientFactory) : IWorkerPool, IWorkerRegistry
{
    const int MaxWorkerPerHost = 10000;

    private static readonly TimeSpan WaitStoppingHostTimeout = TimeSpan.FromSeconds(3); // TODO to config

    private class Ticket(Request req)
    {
        private readonly TaskCompletionSource<Worker> _workerTcs = new();

        public Caps Caps { get; private set; } = req.Caps;

        public Task<Worker> AwaitWorker() => _workerTcs.Task;

        public Task Process(Uri host)
        {
            var processingComplete = new TaskCompletionSource<bool>();
            _workerTcs.SetResult(Worker.Create(() => processingComplete.SetResult(true), host));
            return processingComplete.Task;
        }
    }

    private record HostData(Uri HostUri, HostConfig Config, CancellationTokenSource Cts, Task[] Runners);

    private readonly Channel<Ticket> _queue = Channel.CreateUnbounded<Ticket>();
    private readonly object _denialQueueLock = new object();
    private readonly ILogger<QuotaManager> _logger = logger;
    private readonly IHttpClientFactory _clientFactory = clientFactory;

    private ConcurrentDictionary<Uri, HostData> _runningHosts = new();

    public IEnumerable<Uri> RunningHosts => [.. _runningHosts.Keys];

    public async Task<bool> AddHost(HostConfig config)
    {
        var hostUri = new Uri(config.HostUri);
        await DeleteHost(hostUri);
        _logger.LogInformation($"(Re)starting host '{hostUri}'");

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
            _logger.LogInformation($"Stopping host '{host}'");
            hostInfo.Cts.Cancel();

            var timeout = Task.Delay(WaitStoppingHostTimeout);
            var timeoutExpired = await Task.WhenAny(
                timeout,
                Task.WhenAll(hostInfo.Runners)) == timeout;

            if (timeoutExpired)
            {
                _logger.LogError($"Failed to stop '{host}' gracefully");
                // TODO force stopping agent
            }
            else
            {
                _logger.LogInformation($"Stopped host '{host}'");
            }
            return true;
        }
        return false;
    }

    public async Task<Worker> GetNext(Request req)
    {
        var ticket = new Ticket(req);
        await _queue.Writer.WriteAsync(ticket);

        var worker = await ticket.AwaitWorker();
        return worker;
    }

    public HostConfig[] GetConfig() => _runningHosts.Values.Select(h => h.Config).ToArray();

    private Task[] StartHost(HostConfig config, CancellationToken cancel)
    {
        var hostUri = new Uri(config.HostUri);
        var limit = Math.Min(Math.Max(1, config.Limit), MaxWorkerPerHost);

        var processingPool = Array.ConvertAll(
            new int[limit],
            _ => HostWorker(hostUri, cancel));
        return processingPool;
    }

    async Task HostWorker(Uri hostUri, CancellationToken cancel)
    {
        var inputQueue = _queue.Reader;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var canRead = await inputQueue.WaitToReadAsync(cancel);
                if (canRead && inputQueue.TryRead(out var ticket))
                {
					await ticket.Process(hostUri);
				}
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning($"Worker for '{hostUri}' cancelled");
        }
        // queue is empty, quit the worker
    }
}