namespace Nanto.Testing;

public sealed class ManualUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly Lock _gate = new();
    private readonly DispatcherSynchronizationContext _synchronizationContext;
    private readonly Queue<IWorkItem> _workItems = new();
    private bool _disposed;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _workItems.Count;
            }
        }
    }

    public ManualUiDispatcher()
    {
        _synchronizationContext = new DispatcherSynchronizationContext();
    }

    public bool CheckAccess() => ReferenceEquals(SynchronizationContext.Current, _synchronizationContext);

    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(
            _ =>
            {
                action();
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    public ValueTask<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(_ => ValueTask.FromResult(action()), cancellationToken);
    }

    public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ValueTask(InvokeAsyncCore(action, cancellationToken));
    }

    public ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();

        if (CheckAccess())
        {
            return action(cancellationToken);
        }

        WorkItem<T> workItem;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            workItem = new WorkItem<T>(action, cancellationToken);
            _workItems.Enqueue(workItem);
        }

        return new ValueTask<T>(workItem.Task);
    }

    public async ValueTask<bool> RunNextAsync()
    {
        IWorkItem workItem;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_workItems.TryDequeue(out workItem!))
            {
                return false;
            }
        }

        await PumpAsync(workItem.ExecuteAsync).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DrainAsync()
    {
        while (await RunNextAsync().ConfigureAwait(false))
        {
        }
    }

    public void Dispose()
    {
        IWorkItem[] pendingWork;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pendingWork = [.. _workItems];
            _workItems.Clear();
        }

        foreach (var workItem in pendingWork)
        {
            workItem.Cancel();
        }
    }

    private async Task InvokeAsyncCore(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        await InvokeAsync(
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken);
    }

    private async Task PumpAsync(Func<Task> startWork)
    {
        var task = RunWithDispatcherContext(startWork);
        while (!task.IsCompleted)
        {
            while (_synchronizationContext.TryRunNext())
            {
            }

            if (!task.IsCompleted)
            {
                await Task.WhenAny(task, _synchronizationContext.WaitForCallbackAsync()).ConfigureAwait(false);
            }
        }

        await task.ConfigureAwait(false);
    }

    private T RunWithDispatcherContext<T>(Func<T> action)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private interface IWorkItem
    {
        Func<Task> ExecuteAsync { get; }

        void Cancel();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<CancellationToken, ValueTask<T>> _action;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _state;

        public Func<Task> ExecuteAsync => ExecuteCoreAsync;

        public Task<T> Task => _completion.Task;

        public WorkItem(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
        {
            _action = action;
            _cancellationToken = cancellationToken;

            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.UnsafeRegister(static state => ((WorkItem<T>)state!).CancelFromToken(), this);
                if (Volatile.Read(ref _state) != 0)
                {
                    _cancellationRegistration.Unregister();
                }
            }
        }

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetCanceled();
                _cancellationRegistration.Unregister();
            }
        }

        private async Task ExecuteCoreAsync()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            _cancellationRegistration.Unregister();

            try
            {
                _completion.TrySetResult(await _action(_cancellationToken));
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == _cancellationToken)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref _state, 2);
            }
        }

        private void CancelFromToken()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetCanceled(_cancellationToken);
                _cancellationRegistration.Unregister();
            }
        }
    }

    private sealed class DispatcherSynchronizationContext : SynchronizationContext
    {
        private readonly Lock _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private TaskCompletionSource _callbackAvailable = CreateSignal();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                _callbacks.Enqueue((callback, state));
                _callbackAvailable.TrySetResult();
            }
        }

        public Task WaitForCallbackAsync()
        {
            lock (_gate)
            {
                return _callbacks.Count == 0 ? _callbackAvailable.Task : Task.CompletedTask;
            }
        }

        public bool TryRunNext()
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (_gate)
            {
                if (!_callbacks.TryDequeue(out work))
                {
                    return false;
                }

                if (_callbacks.Count == 0)
                {
                    _callbackAvailable = CreateSignal();
                }
            }

            var previousContext = Current;
            SetSynchronizationContext(this);
            try
            {
                work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previousContext);
            }

            return true;
        }

        private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}