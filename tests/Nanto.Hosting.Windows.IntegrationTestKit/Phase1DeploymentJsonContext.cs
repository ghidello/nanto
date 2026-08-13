using System.Text.Json.Serialization;

namespace Nanto.Hosting.Windows.IntegrationTestKit;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(Phase1DeploymentEvidence))]
internal sealed partial class Phase1DeploymentJsonContext : JsonSerializerContext;
