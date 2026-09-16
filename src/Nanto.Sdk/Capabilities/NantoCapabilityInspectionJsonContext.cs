using System.Text.Json.Serialization;

namespace Nanto.Sdk.Capabilities;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NantoCapabilityInspectionDocument))]
internal sealed partial class NantoCapabilityInspectionJsonContext : JsonSerializerContext;
