using System.Runtime.ExceptionServices;

namespace Nanto.Hosting.Windows;

internal sealed class DispatcherWorkQueue : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ResourceLedger _resourceLedger;
    private readonly Queue<IWorkItem> _workItems = new();
    private int _activeOperations;
    private bool _shutdownRequested;

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

    public int ActiveOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _activeOperations;
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

    public void EnqueueContinuation(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdownRequested && _activeOperations == 0, this);
            _workItems.Enqueue(new ContinuationWorkItem(callback, state, _resourceLedger.Acquire(WindowsResourceKind.DispatcherItem, "DispatcherItem")));
        }
    }

    public ValueTask StartInline(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ValueTask(StartInlineWorkItem(
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken));
    }

    public ValueTask<T> StartInline<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ValueTask<T>(StartInlineWorkItem(action, cancellationToken));
    }

    public bool TryStartNext()
    {
        IWorkItem workItem;
        lock (_gate)
        {
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
        IWorkItem[] canceledWork;
        lock (_gate)
        {
            if (_shutdownRequested)
            {
                return;
            }

            _shutdownRequested = true;
            var pendingWork = _workItems.ToArray();
            _workItems.Clear();
            canceledWork = [.. pendingWork.Where(static workItem => workItem.CancelOnShutdown)];
            foreach (var continuation in pendingWork.Where(static workItem => !workItem.CancelOnShutdown))
            {
                _workItems.Enqueue(continuation);
            }
        }

        List<Exception>? cleanupExceptions = null;
        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (Exception exception)
        {
            AddCleanupException(ref cleanupExceptions, exception);
        }

        foreach (var workItem in canceledWork)
        {
            try
            {
                workItem.CancelBeforeStart(_lifetimeCancellation.Token);
            }
            catch (Exception exception)
            {
                AddCleanupException(ref cleanupExceptions, exception);
            }
        }

        try
        {
            _lifetimeCancellation.Dispose();
        }
        catch (Exception exception)
        {
            AddCleanupException(ref cleanupExceptions, exception);
        }

        if (cleanupExceptions is [var cleanupException])
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }

        if (cleanupExceptions is { Count: > 1 })
        {
            throw new AggregateException("Dispatcher shutdown encountered multiple cleanup failures.", cleanupExceptions);
        }
    }

    private Task<T> EnqueueWorkItem<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        WorkItem<T> workItem;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdownRequested, this);
            _activeOperations++;
            try
            {
                workItem = new WorkItem<T>(
                    action,
                    _resourceLedger.Acquire(WindowsResourceKind.DispatcherItem, "DispatcherItem"),
                    OnOperationCompleted,
                    cancellationToken,
                    _lifetimeCancellation.Token);
            }
            catch
            {
                _activeOperations--;
                throw;
            }

            _workItems.Enqueue(workItem);
        }

        return workItem.Task;
    }

    private Task<T> StartInlineWorkItem<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        WorkItem<T> workItem;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdownRequested, this);
            _activeOperations++;
            try
            {
                workItem = new WorkItem<T>(
                    action,
                    _resourceLedger.Acquire(WindowsResourceKind.DispatcherItem, "DispatcherItem"),
                    OnOperationCompleted,
                    cancellationToken,
                    _lifetimeCancellation.Token);
            }
            catch
            {
                _activeOperations--;
                throw;
            }
        }

        workItem.Start();
        return workItem.Task;
    }

    private void OnOperationCompleted()
    {
        lock (_gate)
        {
            if (_activeOperations <= 0)
            {
                throw new InvalidOperationException("The dispatcher work queue completed more operations than it started.");
            }

            _activeOperations--;
        }
    }

    private static void AddCleanupException(ref List<Exception>? cleanupExceptions, Exception exception)
    {
        cleanupExceptions ??= [];
        cleanupExceptions.Add(exception);
    }

    private interface IWorkItem
    {
        bool CancelOnShutdown { get; }

        void Start();

        void CancelBeforeStart(CancellationToken cancellationToken);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<CancellationToken, ValueTask<T>> _action;
        private readonly CancellationTokenSource _combinedCancellation;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action _operationCompleted;
        private readonly CancellationTokenRegistration _callerCancellationRegistration;
        private readonly CancellationTokenRegistration _lifetimeCancellationRegistration;
        private IDisposable? _resourceLease;
        private int _state;

        public Task<T> Task => _completion.Task;

        public bool CancelOnShutdown => true;

        public WorkItem(
            Func<CancellationToken, ValueTask<T>> action,
            IDisposable resourceLease,
            Action operationCompleted,
            CancellationToken callerCancellationToken,
            CancellationToken lifetimeCancellationToken)
        {
            _action = action;
            _resourceLease = resourceLease;
            _operationCompleted = operationCompleted;
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
                try
                {
                    ReleaseResources();
                    _completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
            }
        }

        private async Task ExecuteAsync()
        {
            T? result = default;
            CancellationToken canceledToken = default;
            Exception? failure = null;
            var canceled = false;
            try
            {
                result = await _action(_combinedCancellation.Token);
            }
            catch (OperationCanceledException exception)
            {
                canceled = true;
                canceledToken = exception.CancellationToken;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Volatile.Write(ref _state, 2);
                try
                {
                    ReleaseResources();
                }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException("Dispatcher work and its cleanup both failed.", failure, exception);
                    canceled = false;
                }
            }

            if (failure is not null)
            {
                _completion.TrySetException(failure);
            }
            else if (canceled)
            {
                _completion.TrySetCanceled(canceledToken);
            }
            else
            {
                _completion.TrySetResult(result!);
            }
        }

        private void ReleaseResources()
        {
            _callerCancellationRegistration.Unregister();
            _lifetimeCancellationRegistration.Unregister();
            _combinedCancellation.Dispose();
            Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
            _operationCompleted();
        }

        private sealed record CancellationState(WorkItem<T> WorkItem, CancellationToken CancellationToken);
    }

    private sealed class ContinuationWorkItem(SendOrPostCallback callback, object? state, IDisposable resourceLease) : IWorkItem
    {
        private IDisposable? _resourceLease = resourceLease;
        private int _state;

        public bool CancelOnShutdown => false;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            try
            {
                callback(state);
            }
            finally
            {
                Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
                Volatile.Write(ref _state, 2);
            }
        }

        public void CancelBeforeStart(CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Synchronization-context continuations cannot be canceled during dispatcher shutdown.");
    }
}
