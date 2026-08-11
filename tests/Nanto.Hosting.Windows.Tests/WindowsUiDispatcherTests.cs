using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowsUiDispatcherTests
{
    [Fact]
    public async Task ExternalInvocationRunsOnlyWhenTheUiThreadDrainsTheQueue()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        var drainRequests = 0;
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, () => drainRequests++);
        var called = false;
        var callbackHadAccess = false;
        SynchronizationContext? callbackContext = null;

        var work = dispatcher.InvokeAsync(
            () =>
            {
                called = true;
                callbackHadAccess = dispatcher.CheckAccess();
                callbackContext = SynchronizationContext.Current;
            },
            TestContext.Current.CancellationToken);

        called.Should().BeFalse();
        dispatcher.PendingCount.Should().Be(1);
        drainRequests.Should().Be(1);

        hasAccess = true;
        dispatcher.Drain();
        hasAccess = false;
        await work;

        called.Should().BeTrue();
        callbackHadAccess.Should().BeTrue();
        callbackContext.Should().BeOfType<DispatcherSynchronizationContext>();
        dispatcher.PendingCount.Should().Be(0);
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task InvocationFromTheUiThreadStartsInline()
    {
        var ledger = new ResourceLedger();
        var hasAccess = true;
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, static () => { });
        var nestedCalled = false;
        Task nestedWork = Task.CompletedTask;

        var outerWork = dispatcher.InvokeAsync(
            () =>
            {
                nestedWork = dispatcher.InvokeAsync(
                    () =>
                    {
                        nestedCalled = true;
                    },
                    TestContext.Current.CancellationToken).AsTask();
                nestedCalled.Should().BeTrue();
            },
            TestContext.Current.CancellationToken);

        await outerWork;
        await nestedWork;
        dispatcher.PendingCount.Should().Be(0);
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task AwaitedCallbackResumesThroughThePrivateSynchronizationContext()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        using var drainRequested = new SemaphoreSlim(0);
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, () => drainRequested.Release());
        var callbackMayContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedWithAccess = false;

        var work = dispatcher.InvokeAsync(
            async _ =>
            {
                await callbackMayContinue.Task;
                resumedWithAccess = dispatcher.CheckAccess();
            },
            TestContext.Current.CancellationToken);

        await drainRequested.WaitAsync(TestContext.Current.CancellationToken);
        Drain();
        callbackMayContinue.SetResult();
        await drainRequested.WaitAsync(TestContext.Current.CancellationToken);
        Drain();
        await work;

        resumedWithAccess.Should().BeTrue();
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);

        void Drain()
        {
            hasAccess = true;
            try
            {
                dispatcher.Drain();
            }
            finally
            {
                hasAccess = false;
            }
        }
    }

    [Fact]
    public async Task ConfigureAwaitFalseLeavesUiAffinityButDispatcherCleanupReturnsToTheUiThread()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        using var drainRequested = new SemaphoreSlim(0);
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, () => drainRequested.Release());
        var callbackMayContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackContinued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedWithAccess = true;

        var work = dispatcher.InvokeAsync(
            async _ =>
            {
                await callbackMayContinue.Task.ConfigureAwait(false);
                resumedWithAccess = dispatcher.CheckAccess();
                callbackContinued.SetResult();
            },
            TestContext.Current.CancellationToken);

        await drainRequested.WaitAsync(TestContext.Current.CancellationToken);
        Drain();
        callbackMayContinue.SetResult();
        await callbackContinued.Task;
        await drainRequested.WaitAsync(TestContext.Current.CancellationToken);

        work.IsCompleted.Should().BeFalse();
        Drain();
        await work;

        resumedWithAccess.Should().BeFalse();
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);

        void Drain()
        {
            hasAccess = true;
            try
            {
                dispatcher.Drain();
            }
            finally
            {
                hasAccess = false;
            }
        }
    }

    [Fact]
    public async Task DisposalLetsRunningCallbacksObserveCancellationAndUnwindOnTheUiThread()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, static () => { });
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObservedWithAccess = false;
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
                    cancellationObservedWithAccess = dispatcher.CheckAccess();
                    throw;
                }
            },
            TestContext.Current.CancellationToken);

        hasAccess = true;
        dispatcher.Drain();
        hasAccess = false;
        await callbackStarted.Task;
        dispatcher.Dispose();

        hasAccess = true;
        dispatcher.Drain();
        hasAccess = false;

        await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        cancellationObservedWithAccess.Should().BeTrue();
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task CallerCancellationLetsRunningCallbacksUnwindOnTheUiThread()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        using var drainRequested = new SemaphoreSlim(0);
        using var callerCancellation = new CancellationTokenSource();
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, () => drainRequested.Release());
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObservedWithAccess = false;
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
                    cancellationObservedWithAccess = dispatcher.CheckAccess();
                    throw;
                }
            },
            callerCancellation.Token);

        await drainRequested.WaitAsync(TestContext.Current.CancellationToken);
        Drain();
        await callbackStarted.Task;
        callerCancellation.Cancel();
        await drainRequested.WaitAsync(TestContext.Current.CancellationToken);
        Drain();

        await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        cancellationObservedWithAccess.Should().BeTrue();
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);

        void Drain()
        {
            hasAccess = true;
            try
            {
                dispatcher.Drain();
            }
            finally
            {
                hasAccess = false;
            }
        }
    }

    [Fact]
    public async Task DisposalRequestsTheFinalDrainBeforePropagatingCancellationCallbackFailure()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        var drainRequests = 0;
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, () => drainRequests++);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationFailure = new InvalidOperationException("cancellation callback failed");
        var work = dispatcher.InvokeAsync(
            async token =>
            {
                using var registration = token.Register(() => throw cancellationFailure);
                callbackStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            TestContext.Current.CancellationToken);

        Drain();
        await callbackStarted.Task;
        var dispose = dispatcher.Invoking(static value => value.Dispose());

        var disposeException = dispose.Should().Throw<AggregateException>().Which;
        disposeException.Flatten().InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(cancellationFailure);
        drainRequests.Should().BeGreaterThan(1);
        Drain();
        await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        dispatcher.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);

        void Drain()
        {
            hasAccess = true;
            try
            {
                dispatcher.Drain();
            }
            finally
            {
                hasAccess = false;
            }
        }
    }

    [Fact]
    public async Task DisposalCancelsQueuedWorkAndRejectsNewInvocations()
    {
        var ledger = new ResourceLedger();
        var dispatcher = new WindowsUiDispatcher(ledger, static () => false, static () => { });
        var called = false;
        var queuedWork = dispatcher.InvokeAsync(() => called = true, TestContext.Current.CancellationToken);

        dispatcher.Dispose();

        await queuedWork.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        called.Should().BeFalse();
        var invoke = () => dispatcher.InvokeAsync(static () => { }, TestContext.Current.CancellationToken);
        invoke.Should().Throw<ObjectDisposedException>();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public void DrainRequiresUiThreadAccessAndRestoresTheCallingContext()
    {
        var ledger = new ResourceLedger();
        var hasAccess = false;
        using var dispatcher = new WindowsUiDispatcher(ledger, () => hasAccess, static () => { });
        var originalContext = SynchronizationContext.Current;

        dispatcher.Invoking(static value => value.Drain()).Should().Throw<InvalidOperationException>().WithMessage("*UI thread*");

        hasAccess = true;
        dispatcher.Drain();

        SynchronizationContext.Current.Should().BeSameAs(originalContext);
    }

    [Fact]
    public void WakeFailureShutsTheDispatcherDownWithoutReplacingTheOriginalException()
    {
        var ledger = new ResourceLedger();
        var failure = new InvalidOperationException("wake failed");
        var requestCount = 0;
        using var dispatcher = new WindowsUiDispatcher(
            ledger,
            static () => false,
            () =>
            {
                requestCount++;
                throw failure;
            });

        var invoke = () => dispatcher.InvokeAsync(static () => { }, TestContext.Current.CancellationToken);

        invoke.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
        requestCount.Should().Be(1);
        var invokeAfterFailure = () => dispatcher.InvokeAsync(static () => { }, TestContext.Current.CancellationToken);
        invokeAfterFailure.Should().Throw<ObjectDisposedException>();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }
}