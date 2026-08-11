namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1LifecycleTransition
{
    public required string Owner { get; init; }

    public required string OldState { get; init; }

    public required string NewState { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
