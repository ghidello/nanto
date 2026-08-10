using AwesomeAssertions;

using Nanto.Testing;

namespace Nanto.Testing.Tests;

public sealed class ManualUiDispatcherTests
{
    [Fact]
    public async Task WorkRemainsQueuedUntilExplicitlyDrained()
    {
        using var dispatcher = new ManualUiDispatcher();
        var called = false;

        var invocation = dispatcher.InvokeAsync(() => called = true, TestContext.Current.CancellationToken);

        called.Should().BeFalse();
        dispatcher.PendingCount.Should().Be(1);

        await dispatcher.DrainAsync();
        await invocation;

        called.Should().BeTrue();
        dispatcher.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task NestedInvocationRunsInlineWithDispatcherAccess()
    {
        using var dispatcher = new ManualUiDispatcher();
        var nestedAccess = false;

        var invocation = dispatcher.InvokeAsync(
            async cancellationToken =>
            {
                dispatcher.CheckAccess().Should().BeTrue();
                await dispatcher.InvokeAsync(() => nestedAccess = dispatcher.CheckAccess(), cancellationToken);
            },
            TestContext.Current.CancellationToken);

        await dispatcher.DrainAsync();
        await invocation;

        nestedAccess.Should().BeTrue();
    }

    [Fact]
    public async Task AsynchronousContinuationReturnsToDispatcher()
    {
        using var dispatcher = new ManualUiDispatcher();
        var continuationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationAccess = false;

        var invocation = dispatcher.InvokeAsync(
            async _ =>
            {
                await continuationGate.Task;
                continuationAccess = dispatcher.CheckAccess();
            },
            TestContext.Current.CancellationToken);

        var runNext = dispatcher.RunNextAsync();
        continuationGate.SetResult();
        (await runNext).Should().BeTrue();
        await invocation;

        continuationAccess.Should().BeTrue();
    }

    [Fact]
    public async Task SuppressedCaptureCompletesAndRestoresCallerContext()
    {
        using var dispatcher = new ManualUiDispatcher();
        var continuationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerContext = new SynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        var continuationAccess = true;
        ValueTask<bool> runNext;

        var invocation = dispatcher.InvokeAsync(
            async _ =>
            {
                await continuationGate.Task.ConfigureAwait(false);
                continuationAccess = dispatcher.CheckAccess();
            },
            TestContext.Current.CancellationToken);

        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            runNext = dispatcher.RunNextAsync();
            SynchronizationContext.Current.Should().BeSameAs(callerContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        continuationGate.SetResult();
        (await runNext).Should().BeTrue();
        await invocation;

        continuationAccess.Should().BeFalse();
    }

    [Fact]
    public async Task PreCanceledWorkNeverRuns()
    {
        using var dispatcher = new ManualUiDispatcher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;

        var invocation = dispatcher.InvokeAsync(() => called = true, cancellation.Token);
        await dispatcher.DrainAsync();

        await invocation.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task CancellationAfterQueueingCompletesBeforeDrain()
    {
        using var dispatcher = new ManualUiDispatcher();
        using var cancellation = new CancellationTokenSource();
        var called = false;

        var invocation = dispatcher.InvokeAsync(() => called = true, cancellation.Token);
        cancellation.Cancel();

        await invocation.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await dispatcher.DrainAsync();

        called.Should().BeFalse();
    }

    [Fact]
    public async Task NestedInvocationAfterDisposalIsRejected()
    {
        using var dispatcher = new ManualUiDispatcher();
        var called = false;

        var invocation = dispatcher.InvokeAsync(
            async _ =>
            {
                dispatcher.Dispose();
                await dispatcher.InvokeAsync(() => called = true, CancellationToken.None);
            },
            TestContext.Current.CancellationToken);

        (await dispatcher.RunNextAsync()).Should().BeTrue();
        await invocation.AsTask().Invoking(static task => task).Should().ThrowAsync<ObjectDisposedException>();

        called.Should().BeFalse();
    }
}