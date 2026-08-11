using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nanto.Hosting.Windows.TestProtocol;

[JsonSerializable(typeof(Phase1TestReport))]
[JsonSerializable(typeof(Phase1TestRequest))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, WriteIndented = true)]
public sealed partial class Phase1TestJsonContext : JsonSerializerContext;
