namespace Nanto.Hosting.Windows.LongRunningIntegrationTests;

public sealed record LongRunningProgress
{
    public required int SchemaVersion { get; init; }

    public required string RunId { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required long ElapsedMilliseconds { get; init; }

    public required int CurrentBlock { get; init; }

    public required int TotalBlocks { get; init; }

    public required int CurrentProcess { get; init; }

    public required int TotalProcesses { get; init; }

    public required int CompletedProcesses { get; init; }

    public required int CompletedHostLifecycleProcesses { get; init; }

    public required int CompletedRendererRecoveryProcesses { get; init; }

    public string? CurrentScenario { get; init; }

    public int? CurrentScenarioIteration { get; init; }
}
