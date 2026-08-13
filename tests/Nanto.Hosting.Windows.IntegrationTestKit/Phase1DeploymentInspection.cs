namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1DeploymentInspection
{
    public required Phase1DeploymentFile[] Files { get; init; }

    public required long TotalBytes { get; init; }
}
