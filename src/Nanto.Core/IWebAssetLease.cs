namespace Nanto;

/// <summary>
/// Represents a fixed, validated URL-path inventory prepared for a web host.
/// </summary>
/// <remarks>
/// Providers define their content-stability guarantees. In particular, a directory-backed lease does not make file bytes immutable.
/// </remarks>
public interface IWebAssetLease : IDisposable
{
    /// <summary>
    /// Gets the absolute directory exposed by the platform web host.
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// Gets the provider-defined version identifier for this lease.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Gets the immutable, validated set of absolute URL paths available for the lifetime of the lease.
    /// </summary>
    IReadOnlySet<string> AssetPaths { get; }
}
