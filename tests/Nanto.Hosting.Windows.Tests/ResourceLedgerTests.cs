using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class ResourceLedgerTests
{
    [Fact]
    public void LeaseTracksActivePeakAndFinalCounts()
    {
        var ledger = new ResourceLedger();
        var firstWindow = ledger.Acquire(WindowsResourceKind.Window, "FirstWindow");
        using var secondWindow = ledger.Acquire(WindowsResourceKind.Window, "SecondWindow");
        using var handle = ledger.Acquire(WindowsResourceKind.NativeHandle, "Handle");

        firstWindow.Dispose();
        firstWindow.Dispose();
        var snapshot = ledger.CaptureSnapshot();

        snapshot.GetActiveCount(WindowsResourceKind.Window).Should().Be(1);
        snapshot.GetPeakCount(WindowsResourceKind.Window).Should().Be(2);
        snapshot.GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(1);
        snapshot.TotalActive.Should().Be(2);
        snapshot.TotalAcquired.Should().Be(3);
        snapshot.TotalReleased.Should().Be(1);
    }

    [Fact]
    public void DisposingEveryLeaseReturnsTheLedgerToZero()
    {
        var ledger = new ResourceLedger();
        var thread = ledger.Acquire(WindowsResourceKind.UiThread, "UiThread");
        var dispatcherItem = ledger.Acquire(WindowsResourceKind.DispatcherItem, "DispatcherItem");

        dispatcherItem.Dispose();
        thread.Dispose();
        var snapshot = ledger.CaptureSnapshot();

        snapshot.TotalActive.Should().Be(0);
        snapshot.TotalAcquired.Should().Be(snapshot.TotalReleased);
        snapshot.GetPeakCount(WindowsResourceKind.UiThread).Should().Be(1);
        snapshot.GetPeakCount(WindowsResourceKind.DispatcherItem).Should().Be(1);
    }

    [Fact]
    public void UnknownResourceKindsAreRejected()
    {
        var ledger = new ResourceLedger();
        var unknownKind = (WindowsResourceKind)int.MaxValue;

        var acquire = () => ledger.Acquire(unknownKind, "Unknown");
        var read = () => ledger.CaptureSnapshot().GetActiveCount(unknownKind);

        acquire.Should().Throw<ArgumentOutOfRangeException>();
        read.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SnapshotRecordsStableLeaseIdentityAndReleaseOrder()
    {
        var ledger = new ResourceLedger(captureOwnershipEvents: true);
        var application = ledger.Acquire(WindowsResourceKind.ApplicationHost, "ApplicationHost");
        var window = ledger.Acquire(WindowsResourceKind.Window, "Window");

        window.Dispose();
        application.Dispose();
        var events = ledger.CaptureSnapshot().Events;

        events.Select(static ledgerEvent => ledgerEvent.Sequence).Should().Equal(1, 2, 3, 4);
        events.Select(static ledgerEvent => (ledgerEvent.Name, ledgerEvent.Acquired)).Should().Equal(
            ("ApplicationHost", true),
            ("Window", true),
            ("Window", false),
            ("ApplicationHost", false));
        events[0].LeaseId.Should().Be(events[3].LeaseId);
        events[1].LeaseId.Should().Be(events[2].LeaseId);
    }

    [Fact]
    public void SnapshotDoesNotRetainOwnershipEventsByDefault()
    {
        var ledger = new ResourceLedger();

        ledger.Acquire(WindowsResourceKind.DispatcherItem, "DispatcherItem").Dispose();

        ledger.CaptureSnapshot().Events.Should().BeEmpty();
    }
}
