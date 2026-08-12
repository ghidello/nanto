namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1TestReport
{
    public required int ProtocolVersion { get; init; }

    public required string Scenario { get; init; }

    public required bool Succeeded { get; init; }

    public required Phase1HostEnvironment HostEnvironment { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long DurationMilliseconds { get; init; }

    public required string[] ReachedCheckpoints { get; init; }

    public required Phase1LifecycleTransition[] LifecycleTransitions { get; init; }

    public required Phase1ResourceLedgerReport InitialResources { get; init; }

    public required Phase1ResourceLedgerReport PeakResources { get; init; }

    public required Phase1ResourceLedgerReport FinalResources { get; init; }

    public required Phase1ResourceOwnershipEvent[] ResourceOwnershipEvents { get; init; }

    public required string RendererRecoveryResult { get; init; }

    public required string[] RetainedArtifactPaths { get; init; }

    public Phase1ObservedFailure? ObservedFailure { get; init; }
}
