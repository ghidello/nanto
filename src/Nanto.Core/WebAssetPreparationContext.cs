namespace Nanto;

public sealed class WebAssetPreparationContext
{
    public string ApplicationId { get; }

    public string ApplicationStorageKey { get; }

    internal WebAssetPreparationContext(string applicationId, string applicationStorageKey)
    {
        ApplicationId = applicationId;
        ApplicationStorageKey = applicationStorageKey;
    }
}