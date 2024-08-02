namespace GridQuota;

public record Caps();
public record Request(Caps Caps, int UserId);

/// <summary>
/// Pool of workers holding particular resource type.
/// </summary>
/// <typeparam name="TResource"></typeparam>
public interface IWorkerPool<TResource>
{
	/// <summary>
	/// Gets the pooled resource. You must release it after use so it gets back to the pool.
	/// </summary>
	/// <param name="req"></param>
	/// <returns></returns>
	Task<ResourceToken<TResource>> GetNext(Request req);
}

public interface IWorkerRegistry
{
	Task<bool> AddHost(HostConfig config);
	Task<bool> DeleteHost(Uri config);

	IEnumerable<Uri> RunningHosts { get; }

	HostConfig[] GetConfig();
}