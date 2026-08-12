using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1TestRunOptions
{
    public required string TestAppPath { get; init; }

    public required string ArtifactRoot { get; init; }

    public required Phase1TestScenario Scenario { get; init; }

    public Phase1TestPresentationMode PresentationMode { get; init; } = Phase1TestPresentationMode.Hidden;

    public string? FailureCheckpoint { get; init; }

    public int IterationCount { get; init; } = 1;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
}
