using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class ResourceLedgerTests
{
    [Fact]
    public void LeaseTracksActivePeakAndFinalCounts()
    {
        var ledger = new ResourceLedger();
        var firstWindow = ledger.Acquire(WindowsResourceKind.Window);
        using var secondWindow = ledger.Acquire(WindowsResourceKind.Window);
        using var handle = ledger.Acquire(WindowsResourceKind.NativeHandle);

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
        var thread = ledger.Acquire(WindowsResourceKind.UiThread);
        var dispatcherItem = ledger.Acquire(WindowsResourceKind.DispatcherItem);

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

        var acquire = () => ledger.Acquire(unknownKind);
        var read = () => ledger.CaptureSnapshot().GetActiveCount(unknownKind);

        acquire.Should().Throw<ArgumentOutOfRangeException>();
        read.Should().Throw<ArgumentOutOfRangeException>();
    }
}