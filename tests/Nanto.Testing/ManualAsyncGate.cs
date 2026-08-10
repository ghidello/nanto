namespace Nanto.Testing;

public sealed class ManualAsyncGate
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _opened = CreateSignal();
    private TaskCompletionSource _reached = CreateSignal();
    private bool _isOpen;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _isOpen;
            }
        }
    }

    public ManualAsyncGate(bool initiallyOpen = true)
    {
        _isOpen = initiallyOpen;
        if (initiallyOpen)
        {
            _opened.TrySetResult();
        }
    }

    public void Open()
    {
        lock (_gate)
        {
            _isOpen = true;
            _opened.TrySetResult();
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (!_isOpen)
            {
                return;
            }

            _isOpen = false;
            _opened = CreateSignal();
            _reached = CreateSignal();
        }
    }

    public Task WaitUntilReachedAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _reached.Task.WaitAsync(cancellationToken);
        }
    }

    internal Task WaitAsync()
    {
        lock (_gate)
        {
            _reached.TrySetResult();
            return _opened.Task;
        }
    }

    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}