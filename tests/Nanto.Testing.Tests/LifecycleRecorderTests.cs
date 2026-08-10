using AwesomeAssertions;

namespace Nanto.Testing.Tests;

public sealed class LifecycleRecorderTests
{
    [Fact]
    public async Task RecorderProducesOrderedImmutableSnapshotsWithCallerTime()
    {
        using var dispatcher = new ManualUiDispatcher();
        var time = new DateTimeOffset(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);
        var recorder = new LifecycleRecorder(new TestingFixture.FixedTimeProvider(time));
        var window = new FakeNantoWindow(dispatcher, new WindowOptions { Title = "Window" }, TimeProvider.System);
        using var subscription = recorder.Attach(window);
        var rendererFailure = new RendererFailedEventArgs
        {
            Kind = RendererFailureKind.Unresponsive,
            Description = "Unresponsive",
            WillAttemptRecovery = true,
            OccurredAt = time,
        };

        var raise = window.RaiseRendererFailureAsync(rendererFailure, TestContext.Current.CancellationToken);
        var close = window.CloseAsync(TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await raise;
        await close;

        var firstSnapshot = recorder.Snapshot();
        firstSnapshot.Select(static record => record.Sequence).Should().Equal(1, 2, 3);
        firstSnapshot.Select(static record => record.Kind).Should().Equal(
            LifecycleRecordKind.RendererFailed,
            LifecycleRecordKind.WindowStateChanged,
            LifecycleRecordKind.WindowStateChanged);
        firstSnapshot.Should().OnlyContain(record => record.RecordedAt == time && record.WindowId == window.Id);

        subscription.Dispose();
        var ignoredRaise = window.RaiseRendererFailureAsync(rendererFailure, TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await ignoredRaise;

        firstSnapshot.Should().HaveCount(3);
        recorder.Snapshot().Should().HaveCount(3);
    }
}