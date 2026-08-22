using Nanto.Hosting;

namespace Nanto.Hosting.Windows;

internal sealed record PreparedWindowsContent
{
    private static readonly IReadOnlySet<string> _noAssetPaths = new HashSet<string>(StringComparer.Ordinal);

    public required Uri StartUri { get; init; }

    public required Uri TrustedOrigin { get; init; }

    public required IReadOnlySet<string> AssetPaths { get; init; }

    public string? AssetRootDirectory { get; init; }

    public bool UsesVirtualHostMapping => AssetRootDirectory is not null;

    public static PreparedWindowsContent Production(IWebAssetLease assets, string initialRoute)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return new PreparedWindowsContent
        {
            StartUri = new Uri(NavigationPolicy.ProductionOrigin, initialRoute),
            TrustedOrigin = NavigationPolicy.ProductionOrigin,
            AssetPaths = assets.AssetPaths,
            AssetRootDirectory = assets.RootDirectory,
        };
    }

    public static PreparedWindowsContent Development(ValidatedDevelopmentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PreparedWindowsContent
        {
            StartUri = content.StartUri,
            TrustedOrigin = content.TrustedOrigin,
            AssetPaths = _noAssetPaths,
        };
    }
}
