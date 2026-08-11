namespace Nanto.Hosting.Windows;

internal sealed class DispatcherWorkQueue : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ResourceLedger _resourceLedger;
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

    public DispatcherWorkQueue(ResourceLedger resourceLedger)
    {
        _resourceLedger = resourceLedger ?? throw new ArgumentNullException(nameof(resourceLedger));
    }

    public ValueTask Enqueue(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ValueTask(EnqueueWorkItem(
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken));
    }

    public ValueTask<T> Enqueue<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ValueTask<T>(EnqueueWorkItem(action, cancellationToken));
    }

    public bool TryStartNext()
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

        // The caller installs the UI synchronization context before starting work so asynchronous callbacks retain affinity.
        workItem.Start();
        return true;
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

        _lifetimeCancellation.Cancel();
        foreach (var workItem in pendingWork)
        {
            workItem.CancelBeforeStart(_lifetimeCancellation.Token);
        }

        _lifetimeCancellation.Dispose();
    }

    private Task<T> EnqueueWorkItem<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        WorkItem<T> workItem;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            workItem = new WorkItem<T>(
                action,
                _resourceLedger.Acquire(WindowsResourceKind.DispatcherItem),
                cancellationToken,
                _lifetimeCancellation.Token);
            _workItems.Enqueue(workItem);
        }

        return workItem.Task;
    }

    private interface IWorkItem
    {
        void Start();

        void CancelBeforeStart(CancellationToken cancellationToken);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<CancellationToken, ValueTask<T>> _action;
        private readonly CancellationTokenSource _combinedCancellation;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _callerCancellationRegistration;
        private readonly CancellationTokenRegistration _lifetimeCancellationRegistration;
        private IDisposable? _resourceLease;
        private int _state;

        public Task<T> Task => _completion.Task;

        public WorkItem(
            Func<CancellationToken, ValueTask<T>> action,
            IDisposable resourceLease,
            CancellationToken callerCancellationToken,
            CancellationToken lifetimeCancellationToken)
        {
            _action = action;
            _resourceLease = resourceLease;
            _combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, lifetimeCancellationToken);
            if (callerCancellationToken.CanBeCanceled)
            {
                _callerCancellationRegistration = callerCancellationToken.UnsafeRegister(
                    static state => ((CancellationState)state!).WorkItem.CancelBeforeStart(((CancellationState)state).CancellationToken),
                    new CancellationState(this, callerCancellationToken));
                if (Volatile.Read(ref _state) != 0)
                {
                    _callerCancellationRegistration.Unregister();
                }
            }

            if (lifetimeCancellationToken.CanBeCanceled)
            {
                _lifetimeCancellationRegistration = lifetimeCancellationToken.UnsafeRegister(
                    static state => ((CancellationState)state!).WorkItem.CancelBeforeStart(((CancellationState)state).CancellationToken),
                    new CancellationState(this, lifetimeCancellationToken));
                if (Volatile.Read(ref _state) != 0)
                {
                    _lifetimeCancellationRegistration.Unregister();
                }
            }
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                _ = ExecuteAsync();
            }
        }

        public void CancelBeforeStart(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetCanceled(cancellationToken);
                ReleaseResources();
            }
        }

        private async Task ExecuteAsync()
        {
            try
            {
                _completion.TrySetResult(await _action(_combinedCancellation.Token));
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref _state, 2);
                ReleaseResources();
            }
        }

        private void ReleaseResources()
        {
            _callerCancellationRegistration.Unregister();
            _lifetimeCancellationRegistration.Unregister();
            _combinedCancellation.Dispose();
            Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
        }

        private sealed record CancellationState(WorkItem<T> WorkItem, CancellationToken CancellationToken);
    }
}