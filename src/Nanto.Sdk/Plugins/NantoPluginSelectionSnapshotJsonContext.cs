using System.Text.Json.Serialization;

namespace Nanto.Sdk.Plugins;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NantoPluginSelectionSnapshotDocument))]
internal sealed partial class NantoPluginSelectionSnapshotJsonContext : JsonSerializerContext;