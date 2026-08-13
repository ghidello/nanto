namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1DeploymentIdentity
{
    public required string TargetFramework { get; init; }

    public required string RuntimeIdentifier { get; init; }

    public required string Architecture { get; init; }
}
