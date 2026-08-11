namespace Nanto.Hosting.Windows;

internal sealed class WindowRegistry
{
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<INantoWindow> _snapshot = Array.AsReadOnly<INantoWindow>([]);

    public int Count => Volatile.Read(ref _snapshot).Count;

    public IReadOnlyList<INantoWindow> Snapshot => Volatile.Read(ref _snapshot);

    public WindowRegistry(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Add(INantoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ThrowIfNotOnUiThread();

        if (Count != 0)
        {
            throw new NotSupportedException("Phase 1 supports only one window.");
        }

        Volatile.Write(ref _snapshot, Array.AsReadOnly([window]));
    }

    public bool Remove(INantoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ThrowIfNotOnUiThread();

        var current = Volatile.Read(ref _snapshot);
        if (current.Count == 0 || !ReferenceEquals(current[0], window))
        {
            return false;
        }

        Volatile.Write(ref _snapshot, Array.AsReadOnly<INantoWindow>([]));
        return true;
    }

    private void ThrowIfNotOnUiThread()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The window registry can be mutated only on the UI thread.");
        }
    }
}