using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nanto.Hosting.Windows.LongRunningIntegrationTests;

[JsonSerializable(typeof(LongRunningSummary))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, WriteIndented = true)]
public sealed partial class LongRunningJsonContext : JsonSerializerContext;
