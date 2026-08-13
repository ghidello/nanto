using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowRegistryTests
{
    [Fact]
    public void AddAndRemovePublishStableSnapshots()
    {
        var dispatcher = new StubDispatcher { HasAccess = true };
        var registry = new WindowRegistry(dispatcher);
        var window = new StubWindow();

        registry.Add(window);
        var populatedSnapshot = registry.Snapshot;
        registry.Remove(window).Should().BeTrue();

        populatedSnapshot.Should().ContainSingle().Which.Should().BeSameAs(window);
        registry.Snapshot.Should().BeEmpty();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void AddingASecondWindowIsRejectedInPhaseOne()
    {
        var registry = new WindowRegistry(new StubDispatcher { HasAccess = true });
        registry.Add(new StubWindow());

        var addSecond = () => registry.Add(new StubWindow());

        addSecond.Should().Throw<NotSupportedException>().WithMessage("*one window*");
    }

    [Fact]
    public void MutationsRequireDispatcherAccess()
    {
        var dispatcher = new StubDispatcher();
        var registry = new WindowRegistry(dispatcher);
        var window = new StubWindow();

        var add = () => registry.Add(window);
        add.Should().Throw<InvalidOperationException>().WithMessage("*UI thread*");

        dispatcher.HasAccess = true;
        registry.Add(window);
        dispatcher.HasAccess = false;
        var remove = () => registry.Remove(window);
        remove.Should().Throw<InvalidOperationException>().WithMessage("*UI thread*");
    }

    private sealed class StubDispatcher : IUiDispatcher
    {
        public bool HasAccess { get; set; }

        public bool CheckAccess() => HasAccess;

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubWindow : INantoWindow
    {
        public WindowId Id { get; } = WindowId.Create();

        public string Title => "Window";

        public WindowSize Size => new(100, 100);

        public WindowState State => WindowState.Running;

        public bool IsVisible => false;

        public event EventHandler<WindowStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<RendererFailedEventArgs>? RendererFailed
        {
            add { }
            remove { }
        }

        public ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SetSizeAsync(WindowSize size, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
