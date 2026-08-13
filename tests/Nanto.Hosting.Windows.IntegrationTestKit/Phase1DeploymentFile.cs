namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1DeploymentFile
{
    public required string RelativePath { get; init; }

    public required long Length { get; init; }

    public required string Sha256 { get; init; }
}
