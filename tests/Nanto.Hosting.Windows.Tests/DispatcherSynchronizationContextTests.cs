using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class DispatcherSynchronizationContextTests
{
    [Fact]
    public void PostDelegatesWithoutRunningTheCallbackInline()
    {
        var callbacks = new Queue<(SendOrPostCallback Callback, object? State)>();
        var context = new DispatcherSynchronizationContext(() => false, (callback, state) => callbacks.Enqueue((callback, state)));
        object? observedState = null;
        var state = new object();

        context.Post(value => observedState = value, state);

        observedState.Should().BeNull();
        var queued = callbacks.Dequeue();
        queued.Callback(queued.State);
        observedState.Should().BeSameAs(state);
    }

    [Fact]
    public void SendRunsInlineOnlyWithDispatcherAccess()
    {
        var hasAccess = true;
        var context = new DispatcherSynchronizationContext(() => hasAccess, static (_, _) => { });
        var called = false;

        context.Send(_ => called = true, null);
        called.Should().BeTrue();

        hasAccess = false;
        var send = () => context.Send(static _ => { }, null);
        send.Should().Throw<NotSupportedException>().WithMessage("*cross-thread*");
    }

    [Fact]
    public void CopyRetainsThePrivateDispatcherContext()
    {
        var context = new DispatcherSynchronizationContext(() => true, static (_, _) => { });

        context.CreateCopy().Should().BeSameAs(context);
    }
}