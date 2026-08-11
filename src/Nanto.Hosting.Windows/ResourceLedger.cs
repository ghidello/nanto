namespace Nanto.Hosting.Windows;

internal sealed class ResourceLedger
{
    private readonly int[] _activeCounts = new int[Enum.GetValues<WindowsResourceKind>().Length];
    private readonly Lock _gate = new();
    private readonly int[] _peakCounts = new int[Enum.GetValues<WindowsResourceKind>().Length];
    private long _totalAcquired;
    private long _totalReleased;

    public ResourceLedgerSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new ResourceLedgerSnapshot(_activeCounts, _peakCounts, _totalAcquired, _totalReleased);
        }
    }

    public IDisposable Acquire(WindowsResourceKind kind)
    {
        ValidateKind(kind);
        lock (_gate)
        {
            var index = (int)kind;
            _activeCounts[index]++;
            _peakCounts[index] = Math.Max(_peakCounts[index], _activeCounts[index]);
            _totalAcquired++;
        }

        return new ResourceLease(this, kind);
    }

    private void Release(WindowsResourceKind kind)
    {
        lock (_gate)
        {
            var index = (int)kind;
            if (_activeCounts[index] <= 0)
            {
                throw new InvalidOperationException($"No active {kind} resource is available to release.");
            }

            _activeCounts[index]--;
            _totalReleased++;
        }
    }

    private static void ValidateKind(WindowsResourceKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The resource kind is not supported.");
        }
    }

    private sealed class ResourceLease(ResourceLedger owner, WindowsResourceKind kind) : IDisposable
    {
        private ResourceLedger? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(kind);
        }
    }
}