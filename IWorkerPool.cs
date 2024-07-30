using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GridQuota;

public record Caps();
public record Request(Caps Caps);

public class Worker : IDisposable
{
    private readonly Action _setComplete;
    public Uri Host { get; private set; }

    private Worker(Action setComplete, Uri host) { _setComplete = setComplete; Host = host; }
    public void Dispose() => _setComplete?.Invoke();

    public static Worker Create(Action setComplete, Uri host) => new(setComplete, host);
}

public interface IWorkerPool
{
    Task<Worker> GetNext(Request req);
}

public interface IWorkerRegistry
{
    Task<bool> AddHost(HostConfig config);
    Task<bool> DeleteHost(Uri config);

    IEnumerable<Uri> RunningHosts { get; }

    HostConfig[] GetConfig();
}