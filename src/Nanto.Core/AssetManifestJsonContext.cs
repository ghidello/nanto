using System.Text.Json.Serialization;

namespace Nanto;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(AssetManifestDocument))]
[JsonSerializable(typeof(CachedAssetManifestDocument))]
internal sealed partial class AssetManifestJsonContext : JsonSerializerContext;

internal sealed class AssetManifestDocument
{
    public required int SchemaVersion { get; init; }

    public required AssetManifestEntry[] Assets { get; init; }
}

internal sealed class AssetManifestEntry
{
    public required string Path { get; init; }

    public required string ResourceName { get; init; }

    public required long Length { get; init; }

    public required string Sha256 { get; init; }
}

internal sealed class CachedAssetManifestDocument
{
    public required int SchemaVersion { get; init; }

    public required string ApplicationId { get; init; }

    public required string BundleHash { get; init; }

    public required CachedAssetManifestEntry[] Assets { get; init; }
}

internal sealed class CachedAssetManifestEntry
{
    public required string Path { get; init; }

    public required long Length { get; init; }

    public required string Sha256 { get; init; }
}