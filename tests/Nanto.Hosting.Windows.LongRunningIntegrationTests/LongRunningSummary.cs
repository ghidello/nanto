using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.LongRunningIntegrationTests;

public sealed record LongRunningSummary
{
    public required int SchemaVersion { get; init; }

    public required string RunId { get; init; }

    public required LongRunningStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long DurationMilliseconds { get; init; }

    public required int RequestedBlocks { get; init; }

    public required int CompletedBlocks { get; init; }

    public required int RequestedHostLifecycleProcesses { get; init; }

    public required int CompletedHostLifecycleProcesses { get; init; }

    public required int RequestedRendererRecoveryProcesses { get; init; }

    public required int CompletedRendererRecoveryProcesses { get; init; }

    public required LongRunningBlockSummary[] Blocks { get; init; }

    public required Phase1ResourceCount[] MaximumObservedResources { get; init; }

    public required string SdkVersion { get; init; }

    public required string TestAppLaunchKind { get; init; }

    public required Phase1HostEnvironment HostEnvironment { get; init; }

    public LongRunningFailure? FirstFailure { get; init; }

    public required bool FinalApplicationRootDeleted { get; init; }

    public string? FinalApplicationRootDeletionFailure { get; init; }
}

public sealed record LongRunningBlockSummary
{
    public required int BlockNumber { get; init; }

    public required int CompletedHostLifecycleProcesses { get; init; }

    public required int CompletedRendererRecoveryProcesses { get; init; }

    public required long DurationMilliseconds { get; init; }
}

public sealed record LongRunningFailure
{
    public required int BlockNumber { get; init; }

    public required int Iteration { get; init; }

    public required string Scenario { get; init; }

    public required string Reason { get; init; }

    public required string RelativeArtifactPath { get; init; }
}

public enum LongRunningStatus
{
    Passed,
    Failed,
    TimedOut,
    Canceled,
}
