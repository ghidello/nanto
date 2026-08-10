namespace Nanto;

public interface IWebAssetProvider
{
    ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default);
}