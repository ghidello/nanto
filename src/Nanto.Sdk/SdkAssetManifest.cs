using System.Text.Json.Serialization;

namespace Nanto.Sdk;

internal sealed record SdkAssetManifestDocument
{
    public required int SchemaVersion { get; init; }

    public required SdkAssetManifestEntry[] Assets { get; init; }
}

internal sealed record SdkAssetManifestEntry
{
    public required string Path { get; init; }

    public required string ResourceName { get; init; }

    public required long Length { get; init; }

    public required string Sha256 { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SdkAssetManifestDocument))]
internal sealed partial class SdkAssetManifestJsonContext : JsonSerializerContext;
