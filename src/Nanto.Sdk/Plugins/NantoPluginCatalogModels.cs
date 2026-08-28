namespace Nanto.Sdk.Plugins;

internal sealed record NantoPluginManifestInput(
    string ManifestPath,
    string DisplayPath,
    string PackageRoot,
    string PackageId,
    string PackageVersion,
    IReadOnlyDictionary<string, string> PackageDependencies,
    IReadOnlySet<string> PackageFiles);

internal sealed record NantoPluginCatalogDocument
{
    public int SchemaVersion { get; init; } = 1;

    public required string Fingerprint { get; init; }

    public required NantoPluginCatalogEntry[] Plugins { get; init; }
}

internal sealed record NantoPluginCatalogEntry
{
    public required string Id { get; init; }

    public required string PackageId { get; init; }

    public required string PackageVersion { get; init; }

    public required string RuntimeCompatibility { get; init; }

    public string? IncompatibleDependencyPackage { get; init; }

    public string? IncompatibleDependencyVersion { get; init; }

    public string? CompatibilityReason { get; init; }

    public required string[] Dependencies { get; init; }

    public required NantoPluginPermissionEntry[] Permissions { get; init; }

    public string? FrontendModule { get; init; }

    public string? FrontendModuleSha256 { get; init; }

    public required string ManifestSha256 { get; init; }

    public required string CatalogFingerprint { get; init; }
}

internal sealed record NantoPluginPermissionEntry
{
    public required string Identifier { get; init; }

    public required string[] Members { get; init; }

    public string? ScopeSchema { get; init; }

    public string? ScopeSchemaSha256 { get; init; }
}