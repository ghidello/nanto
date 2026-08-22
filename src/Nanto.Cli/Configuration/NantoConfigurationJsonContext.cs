using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nanto.Cli.Configuration;

[JsonSourceGenerationOptions(
    AllowTrailingCommas = false,
    PropertyNameCaseInsensitive = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(NantoConfiguration))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
internal sealed partial class NantoConfigurationJsonContext : JsonSerializerContext;
