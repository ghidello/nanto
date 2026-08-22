using System.Text.Json.Serialization;

namespace Nanto.Cli.TestProtocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Phase3LifecycleEvent))]
internal sealed partial class Phase3LifecycleJsonContext : JsonSerializerContext;
