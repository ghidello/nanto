namespace Nanto;

/// <summary>
/// Supplies canonical application identity and host-owned storage information to an asset provider.
/// </summary>
/// <remarks>Applications and providers can inspect this context but only a validated host configuration can create it.</remarks>
public sealed class WebAssetPreparationContext
{
    /// <summary>
    /// Gets the canonical application identity associated with this preparation.
    /// </summary>
    public string ApplicationId { get; }

    /// <summary>
    /// Gets the collision-resistant storage key associated with the application identity.
    /// </summary>
    public string ApplicationStorageKey { get; }

    /// <summary>
    /// Gets the absolute application storage root prepared and validated by the host.
    /// </summary>
    public string ApplicationRootDirectory { get; }

    internal WebAssetPreparationContext(string applicationId, string applicationStorageKey, string applicationRootDirectory)
    {
        ApplicationId = applicationId;
        ApplicationStorageKey = applicationStorageKey;
        ApplicationRootDirectory = applicationRootDirectory;
    }
}