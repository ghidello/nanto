namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1ResourceLedgerReport
{
    public required Phase1ResourceCount[] Resources { get; init; }

    public required long TotalAcquired { get; init; }

    public required long TotalReleased { get; init; }

    public required int TotalActive { get; init; }
}
