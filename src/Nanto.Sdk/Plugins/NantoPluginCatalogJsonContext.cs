using System.Text.Json.Serialization;

namespace Nanto.Sdk.Plugins;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NantoPluginCatalogDocument))]
internal sealed partial class NantoPluginCatalogJsonContext : JsonSerializerContext;