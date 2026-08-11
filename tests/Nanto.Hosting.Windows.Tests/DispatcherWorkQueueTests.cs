using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class DispatcherWorkQueueTests
{
    [Fact]
    public async Task WorkStartsInFifoOrderOnlyWhenExplicitlyPumped()
    {
        var ledger = new ResourceLedger();
        using var queue = new DispatcherWorkQueue(ledger);
        var order = new List<int>();
        var first = queue.Enqueue(_ => RecordAsync(1), TestContext.Current.CancellationToken);
        var second = queue.Enqueue(_ => RecordAsync(2), TestContext.Current.CancellationToken);
        var third = queue.Enqueue(_ => RecordAsync(3), TestContext.Current.CancellationToken);

        order.Should().BeEmpty();
        queue.PendingCount.Should().Be(3);
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(3);

        queue.TryStartNext().Should().BeTrue();
        queue.TryStartNext().Should().BeTrue();
        queue.TryStartNext().Should().BeTrue();
        queue.TryStartNext().Should().BeFalse();
        await first;
        await second;
        await third;

        order.Should().Equal(1, 2, 3);
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(0);

        ValueTask RecordAsync(int value)
        {
            order.Add(value);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task CallerCancellationBeforeStartSkipsTheCallback()
    {
        var ledger = new ResourceLedger();
        using var queue = new DispatcherWorkQueue(ledger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;

        var work = queue.Enqueue(_ =>
        {
            called = true;
            return ValueTask.CompletedTask;
        }, cancellation.Token);
        queue.TryStartNext().Should().BeTrue();

        var exception = (await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>()).Which;
        exception.CancellationToken.Should().Be(cancellation.Token);
        called.Should().BeFalse();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task DisposalCancelsRunningWorkThroughTheLifetimeToken()
    {
        var ledger = new ResourceLedger();
        var queue = new DispatcherWorkQueue(ledger);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = queue.Enqueue(
            async token =>
            {
                callbackStarted.SetResult();
                var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetResult(), cancellationObserved);
                await cancellationObserved.Task;
                token.ThrowIfCancellationRequested();
            },
            TestContext.Current.CancellationToken);

        queue.TryStartNext().Should().BeTrue();
        await callbackStarted.Task;
        queue.Dispose();

        await work.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(0);
    }

    [Fact]
    public async Task DisposalFinishesCleanupBeforePropagatingCancellationCallbackFailure()
    {
        var ledger = new ResourceLedger();
        var queue = new DispatcherWorkQueue(ledger);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationFailure = new InvalidOperationException("cancellation callback failed");
        var runningWork = queue.Enqueue(
            async token =>
            {
                var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, token);
                using var registration = token.Register(() => throw cancellationFailure);
                callbackStarted.SetResult();
                await cancellation;
            },
            TestContext.Current.CancellationToken);
        var queuedWork = queue.Enqueue(static _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        queue.TryStartNext().Should().BeTrue();
        await callbackStarted.Task;
        var dispose = queue.Invoking(static value => value.Dispose());

        var disposeException = dispose.Should().Throw<AggregateException>().Which;
        disposeException.Flatten().InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(cancellationFailure);
        await runningWork.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await queuedWork.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        queue.ActiveOperationCount.Should().Be(0);
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(0);
        queue.Invoking(static value => value.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task CallbackFailurePropagatesAndReleasesItsLedgerLease()
    {
        var ledger = new ResourceLedger();
        using var queue = new DispatcherWorkQueue(ledger);
        var failure = new InvalidOperationException("callback failed");
        var work = queue.Enqueue(_ => ValueTask.FromException(failure), TestContext.Current.CancellationToken);

        queue.TryStartNext().Should().BeTrue();
        var exception = (await work.AsTask().Invoking(static task => task).Should().ThrowAsync<InvalidOperationException>()).Which;

        exception.Should().BeSameAs(failure);
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(0);
    }

    [Fact]
    public async Task DisposalCancelsQueuedWorkAndRejectsNewWork()
    {
        var ledger = new ResourceLedger();
        var queue = new DispatcherWorkQueue(ledger);
        var called = false;
        var queuedWork = queue.Enqueue(_ =>
        {
            called = true;
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        queue.Dispose();

        await queuedWork.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        called.Should().BeFalse();
        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
        var enqueue = () => queue.Enqueue(static _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        enqueue.Should().Throw<ObjectDisposedException>();
        queue.TryStartNext().Should().BeFalse();
    }

    [Fact]
    public void ShutdownPreservesAlreadyQueuedSynchronizationContextContinuations()
    {
        var ledger = new ResourceLedger();
        var queue = new DispatcherWorkQueue(ledger);
        var called = false;
        queue.EnqueueContinuation(_ => called = true, null);

        queue.Dispose();
        queue.TryStartNext().Should().BeTrue();

        called.Should().BeTrue();
        queue.TryStartNext().Should().BeFalse();
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(0);
    }

    [Fact]
    public async Task InlineWorkStartsImmediatelyAndUsesTheSameLifetime()
    {
        var ledger = new ResourceLedger();
        var queue = new DispatcherWorkQueue(ledger);
        var called = false;

        var work = queue.StartInline(_ =>
        {
            called = true;
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        called.Should().BeTrue();
        await work;
        queue.PendingCount.Should().Be(0);
        ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.DispatcherItem).Should().Be(0);
        queue.Dispose();
    }
}