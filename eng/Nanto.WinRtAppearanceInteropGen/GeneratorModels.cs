using System.Text.Json.Serialization;

namespace Nanto.WinRtAppearanceInteropGen;

internal sealed record AppearanceSpecification
{
    public int SchemaVersion { get; init; }

    public required string TargetFramework { get; init; }

    public required string TargetingPackVersion { get; init; }

    public required string Contract { get; init; }

    public required string FoundationContract { get; init; }

    public required string ProjectionRuntime { get; init; }

    public required string RuntimeClass { get; init; }

    public required string Interface { get; init; }

    public required string[] Members { get; init; }

    public required string EventHandler { get; init; }
}

internal sealed record GenerationManifest
{
    public int SchemaVersion { get; init; } = 1;

    public required string TargetFramework { get; init; }

    public required string TargetingPackVersion { get; init; }

    public required ManifestInput[] Inputs { get; init; }

    public required ManifestRuntimeClass RuntimeClass { get; init; }

    public required ManifestInterface Interface { get; init; }

    public required ManifestEventHandler EventHandler { get; init; }

    public required ManifestOutput Output { get; init; }
}

internal sealed record ManifestInput(string Path, string Sha256);

internal sealed record ManifestRuntimeClass(string Name, string DefaultInterface, string DefaultInterfaceIid);

internal sealed record ManifestInterface(string Name, string Iid, ManifestSlot[] VtablePrefix);

internal sealed record ManifestSlot(string Name, int Slot, string Signature);

internal sealed record ManifestEventHandler(string Name, string Signature, string Iid);

internal sealed record ManifestOutput(string Path, int ByteLength, string Sha256, string Encoding, string LineEndings);

[JsonSourceGenerationOptions(
    AllowDuplicateProperties = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AppearanceSpecification))]
[JsonSerializable(typeof(GenerationManifest))]
internal sealed partial class GeneratorJsonContext : JsonSerializerContext;
