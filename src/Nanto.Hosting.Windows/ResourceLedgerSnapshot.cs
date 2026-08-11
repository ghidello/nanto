namespace Nanto.Hosting.Windows;

internal sealed class ResourceLedgerSnapshot
{
    private readonly int[] _activeCounts;
    private readonly int[] _peakCounts;

    public long TotalAcquired { get; }

    public long TotalReleased { get; }

    public int TotalActive => _activeCounts.Sum();

    internal ResourceLedgerSnapshot(int[] activeCounts, int[] peakCounts, long totalAcquired, long totalReleased)
    {
        _activeCounts = (int[])activeCounts.Clone();
        _peakCounts = (int[])peakCounts.Clone();
        TotalAcquired = totalAcquired;
        TotalReleased = totalReleased;
    }

    public int GetActiveCount(WindowsResourceKind kind)
    {
        ValidateKind(kind);
        return _activeCounts[(int)kind];
    }

    public int GetPeakCount(WindowsResourceKind kind)
    {
        ValidateKind(kind);
        return _peakCounts[(int)kind];
    }

    private static void ValidateKind(WindowsResourceKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The resource kind is not supported.");
        }
    }
}