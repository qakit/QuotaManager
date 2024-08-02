namespace GridQuota;

/// <summary>
/// Given a number or resources, this class provides a way to acquire and release them in such a way quotas are not exceeded.
/// </summary>
public class QuotaWatcher(int maxResources) : IDisposable
{
	private readonly SemaphoreSlim _gate = new SemaphoreSlim(maxResources, maxResources);

    /// <summary>
    /// Acquires a resource. If no resources are available, waits until one is released.
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public async Task Acquire(CancellationToken cancel)
	{
		await _gate.WaitAsync(cancel);
	}

	/// <summary>
	/// Releases a resource.
	/// </summary>
	public void Release()
	{
		_gate.Release();
	}

	/// <summary>
	/// Gets the maximal number of resources.
	/// </summary>
	public int Quota => maxResources;
	/// <summary>
	/// Gets number of available resources.
	/// </summary>
	public int Available => _gate.CurrentCount;

	public int Used => maxResources - _gate.CurrentCount;

    public void Dispose()
    {
        _gate.Dispose();
    }
}
