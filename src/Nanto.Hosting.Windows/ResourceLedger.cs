namespace Nanto.Hosting.Windows;

internal sealed class ResourceLedger
{
    private readonly int[] _activeCounts = new int[Enum.GetValues<WindowsResourceKind>().Length];
    private readonly bool _captureOwnershipEvents;
    private readonly List<ResourceLedgerEvent> _events = [];
    private readonly Lock _gate = new();
    private readonly int[] _peakCounts = new int[Enum.GetValues<WindowsResourceKind>().Length];
    private long _totalAcquired;
    private long _totalReleased;
    private long _eventSequence;
    private long _leaseSequence;

    public ResourceLedger(bool captureOwnershipEvents = false)
    {
        _captureOwnershipEvents = captureOwnershipEvents;
    }

    public ResourceLedgerSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new ResourceLedgerSnapshot(_activeCounts, _peakCounts, _totalAcquired, _totalReleased, _events);
        }
    }

    public IDisposable Acquire(WindowsResourceKind kind, string name)
    {
        ValidateKind(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        long leaseId;
        lock (_gate)
        {
            var index = (int)kind;
            leaseId = ++_leaseSequence;
            _activeCounts[index]++;
            _peakCounts[index] = Math.Max(_peakCounts[index], _activeCounts[index]);
            _totalAcquired++;
            if (_captureOwnershipEvents)
            {
                _events.Add(new ResourceLedgerEvent(++_eventSequence, leaseId, kind, name, Acquired: true));
            }
        }

        return new ResourceLease(this, leaseId, kind, name);
    }

    private void Release(long leaseId, WindowsResourceKind kind, string name)
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
            if (_captureOwnershipEvents)
            {
                _events.Add(new ResourceLedgerEvent(++_eventSequence, leaseId, kind, name, Acquired: false));
            }
        }
    }

    private static void ValidateKind(WindowsResourceKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The resource kind is not supported.");
        }
    }

    private sealed class ResourceLease(ResourceLedger owner, long leaseId, WindowsResourceKind kind, string name) : IDisposable
    {
        private ResourceLedger? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(leaseId, kind, name);
        }
    }
}
