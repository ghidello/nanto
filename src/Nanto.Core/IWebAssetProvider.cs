namespace Nanto;

/// <summary>
/// Prepares a validated asset inventory for a platform web host.
/// </summary>
public interface IWebAssetProvider
{
    /// <summary>
    /// Prepares an asset lease within the host-validated application storage boundary.
    /// </summary>
    ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default);
}