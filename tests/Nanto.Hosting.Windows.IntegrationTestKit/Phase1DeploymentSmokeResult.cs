namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1DeploymentSmokeResult
{
    public required string Scenario { get; init; }

    public required bool Succeeded { get; init; }

    public required long ElapsedMilliseconds { get; init; }

    public required int? ExitCode { get; init; }

    public required int FinalActiveResourceCount { get; init; }
}
