using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowsUiThreadTests
{
    [Fact]
    public async Task DedicatedThreadIsStaAndDrainsDispatcherWork()
    {
        var ledger = new ResourceLedger();
        await using var uiThread = new WindowsUiThread(ledger);
        var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
        var callingThreadId = Environment.CurrentManagedThreadId;
        var callbackThreadId = 0;
        var apartmentState = ApartmentState.Unknown;

        await dispatcher.InvokeAsync(
            () =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                apartmentState = Thread.CurrentThread.GetApartmentState();
            },
            TestContext.Current.CancellationToken);

        callbackThreadId.Should().NotBe(callingThreadId);
        apartmentState.Should().Be(ApartmentState.STA);
        dispatcher.CheckAccess().Should().BeFalse();
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.UiThread).Should().Be(1);
    }

    [Fact]
    public async Task StopCancelsRunningWorkAndReleasesEveryOwnedResource()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObservedOnUiThread = false;
        var work = dispatcher.InvokeAsync(
            async token =>
            {
                callbackStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancellationObservedOnUiThread = dispatcher.CheckAccess();
                    throw;
                }
            },
            TestContext.Current.CancellationToken);

        await callbackStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        uiThread.RequestStop();
        await uiThread.Completion.WaitAsync(TestContext.Current.CancellationToken);

        await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        cancellationObservedOnUiThread.Should().BeTrue();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
        await uiThread.Invoking(static value => value.DisposeAsync().AsTask()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopBeforeDispatcherPublicationStillTerminatesTheThread()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);

        uiThread.RequestStop();
        await uiThread.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);

        var invoke = () => dispatcher.InvokeAsync(static () => { }, TestContext.Current.CancellationToken);
        invoke.Should().Throw<ObjectDisposedException>();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
        await uiThread.DisposeAsync();
    }

    [Fact]
    public async Task StopReportsCancellationCallbackFailureAfterReleasingTheUiThread()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationFailure = new InvalidOperationException("cancellation callback failed");
        var work = dispatcher.InvokeAsync(
            async token =>
            {
                var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, token);
                using var registration = token.Register(() => throw cancellationFailure);
                callbackStarted.SetResult();
                await cancellation;
            },
            TestContext.Current.CancellationToken);

        await callbackStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        uiThread.RequestStop();
        var completion = uiThread.Completion.Invoking(static task => task.WaitAsync(TestContext.Current.CancellationToken));

        var completionException = (await completion.Should().ThrowAsync<AggregateException>()).Which;
        completionException.Flatten().InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(cancellationFailure);
        await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task DispatcherContinuationFailureDoesNotAbandonWorkQueuedBehindIt()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
        var trailingContinuationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationFailure = new InvalidOperationException("dispatcher continuation failed");

        await dispatcher.InvokeAsync(
            () =>
            {
                var context = SynchronizationContext.Current!;
                context.Post(
                    _ =>
                    {
                        context.Post(static state => ((TaskCompletionSource)state!).SetResult(), trailingContinuationRan);
                        throw continuationFailure;
                    },
                    null);
            },
            TestContext.Current.CancellationToken);

        await trailingContinuationRan.Task.WaitAsync(TestContext.Current.CancellationToken);
        var completionException = (await uiThread.Completion.Invoking(static task => task).Should().ThrowAsync<InvalidOperationException>()).Which;

        completionException.Should().BeSameAs(continuationFailure);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }
}