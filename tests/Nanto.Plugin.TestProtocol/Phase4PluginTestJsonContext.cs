using System.Text.Json.Serialization;

namespace Nanto.Plugin.TestProtocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Phase4PluginLifecycleEvent))]
internal sealed partial class Phase4PluginTestJsonContext : JsonSerializerContext;