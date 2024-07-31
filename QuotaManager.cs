using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace GridQuota;

public record HostData(Uri HostUri, HostConfig Config, CancellationTokenSource Cts, Task[] Runners);

public class QuotaManager(ILogger<QuotaManager> _logger, IOptions<AppConfig> _appConfig) : IWorkerPool, IWorkerRegistry
{
    const int MaxWorkerPerHost = 10000;

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

    private readonly Channel<Ticket> _queue = Channel.CreateUnbounded<Ticket>();

    private readonly ConcurrentDictionary<Uri, HostData> _runningHosts = new();

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
            _logger.LogWarning("Worker for '{Host}' cancelled", hostUri);
        }
        // queue is empty, quit the worker
    }
}