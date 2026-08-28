namespace Nanto.Sdk.Plugins;

internal sealed record NantoPluginSelectionSnapshotDocument
{
    public int SchemaVersion { get; init; } = 1;

    public required string Fingerprint { get; init; }

    public required string HostProject { get; init; }

    public required string TargetFramework { get; init; }

    public required string Configuration { get; init; }

    public required string AssetsFileSha256 { get; init; }

    public required string CatalogFingerprint { get; init; }

    public required NantoPluginSelectionProperty[] RestoreProperties { get; init; }

    public required NantoPluginSelectionInput[] RestoreInputs { get; init; }

    public required NantoPluginCatalogEntry[] Plugins { get; init; }
}

internal sealed record NantoPluginSelectionProperty
{
    public required string Name { get; init; }

    public required string ValueSha256 { get; init; }
}

internal sealed record NantoPluginSelectionInput
{
    public required string Identity { get; init; }

    public required string Path { get; init; }

    public required string Sha256 { get; init; }
}