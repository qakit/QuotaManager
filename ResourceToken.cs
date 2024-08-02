namespace GridQuota;

/// <summary>
/// Represents an access token for resource.
/// </summary>
public class ResourceToken<TResource>(TResource res, Action releaseResource) : IDisposable
{
	private readonly Action _setComplete = releaseResource;
	public TResource Resource { get; private set; } = res;

	public void Dispose() => _setComplete?.Invoke();
}
