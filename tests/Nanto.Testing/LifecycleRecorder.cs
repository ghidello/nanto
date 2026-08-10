namespace Nanto.Testing;

public sealed class LifecycleRecorder
{
    private readonly Lock _gate = new();
    private readonly List<LifecycleRecord> _records = [];
    private readonly TimeProvider _timeProvider;
    private long _nextSequence;

    public LifecycleRecorder(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public IDisposable Attach(INantoApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        EventHandler<ApplicationStateChangedEventArgs> handler = (_, eventArgs) => Record(
            LifecycleRecordKind.ApplicationStateChanged,
            null,
            eventArgs);
        host.StateChanged += handler;
        return new Subscription(() => host.StateChanged -= handler);
    }

    public IDisposable Attach(INantoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        EventHandler<WindowStateChangedEventArgs> stateHandler = (_, eventArgs) => Record(
            LifecycleRecordKind.WindowStateChanged,
            window.Id,
            eventArgs);
        EventHandler<RendererFailedEventArgs> rendererHandler = (_, eventArgs) => Record(
            LifecycleRecordKind.RendererFailed,
            window.Id,
            eventArgs);
        window.StateChanged += stateHandler;
        window.RendererFailed += rendererHandler;
        return new Subscription(
            () =>
            {
                window.StateChanged -= stateHandler;
                window.RendererFailed -= rendererHandler;
            });
    }

    public IReadOnlyList<LifecycleRecord> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly([.. _records]);
        }
    }

    private void Record(LifecycleRecordKind kind, WindowId? windowId, EventArgs eventArgs)
    {
        lock (_gate)
        {
            _records.Add(new LifecycleRecord(++_nextSequence, kind, _timeProvider.GetUtcNow(), windowId, eventArgs));
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}