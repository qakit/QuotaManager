namespace GridQuota;

public record Caps();
public record Request(Caps Caps, int UserId);
public record WorkerInfo(Uri HostUri);


/// <summary>
/// Represents an access token for resource.
/// </summary>
public class JobToken(WorkerInfo worker, Request request, Action<JobToken> onDispose) : IDisposable
{
	public WorkerInfo Worker => worker;
	public Request Request => request;

	public void Dispose() => onDispose?.Invoke(this);
}


/// <summary>
/// Pool of workers holding particular resource type.
/// </summary>
/// <typeparam name="TResource"></typeparam>
public interface IWorkerPool
{
	/// <summary>
	/// Gets the pooled resource. You must release it after use so it gets back to the pool.
	/// </summary>
	/// <param name="req"></param>
	/// <returns></returns>
	Task<JobToken> GetNext(Request req, CancellationToken cancel);
}

public interface IWorkerRegistry
{
	Task<bool> AddHost(HostConfig config);
	Task<bool> DeleteHost(Uri config);

	IEnumerable<Uri> RunningHosts { get; }

	HostConfig[] GetConfig();
}