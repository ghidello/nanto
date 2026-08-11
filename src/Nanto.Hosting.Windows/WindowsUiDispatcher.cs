using System.Runtime.ExceptionServices;

namespace Nanto.Hosting.Windows;

internal sealed class WindowsUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly Func<bool> _checkAccess;
    private readonly DispatcherSynchronizationContext _synchronizationContext;
    private readonly Action _requestDrain;
    private readonly DispatcherWorkQueue _workQueue;
    private int _disposed;

    public int PendingCount => _workQueue.PendingCount;

    public int ActiveOperationCount => _workQueue.ActiveOperationCount;

    public WindowsUiDispatcher(ResourceLedger resourceLedger, Func<bool> checkAccess, Action requestDrain)
    {
        ArgumentNullException.ThrowIfNull(resourceLedger);
        _checkAccess = checkAccess ?? throw new ArgumentNullException(nameof(checkAccess));
        _requestDrain = requestDrain ?? throw new ArgumentNullException(nameof(requestDrain));
        _workQueue = new DispatcherWorkQueue(resourceLedger);
        _synchronizationContext = new DispatcherSynchronizationContext(CheckAccess, PostContinuation);
    }

    public bool CheckAccess() => _checkAccess();

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (CheckAccess())
        {
            return RunWithContext(() => _workQueue.StartInline(action, cancellationToken));
        }

        var work = _workQueue.Enqueue(action, cancellationToken);
        RequestDrainOrShutdown();
        return work;
    }

    public ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (CheckAccess())
        {
            return RunWithContext(() => _workQueue.StartInline(action, cancellationToken));
        }

        var work = _workQueue.Enqueue(action, cancellationToken);
        RequestDrainOrShutdown();
        return work;
    }

    public void Drain()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("Dispatcher work can be drained only on the UI thread.");
        }

        RunWithContext(
            () =>
            {
                while (_workQueue.TryStartNext())
                {
                }
            });
    }

    public void Dispose()
    {
        var shutdownStarted = false;
        Exception? shutdownException = null;
        try
        {
            shutdownStarted = BeginShutdown();
        }
        catch (Exception exception)
        {
            shutdownStarted = true;
            shutdownException = exception;
        }

        if (!shutdownStarted)
        {
            return;
        }

        try
        {
            _requestDrain();
        }
        catch (Exception requestException) when (shutdownException is not null)
        {
            throw new AggregateException("Dispatcher shutdown and its drain request both failed.", shutdownException, requestException);
        }

        if (shutdownException is not null)
        {
            ExceptionDispatchInfo.Capture(shutdownException).Throw();
        }
    }

    private void PostContinuation(SendOrPostCallback callback, object? state)
    {
        _workQueue.EnqueueContinuation(callback, state);
        RequestDrainOrShutdown();
    }

    private void RequestDrainOrShutdown()
    {
        try
        {
            _requestDrain();
        }
        catch (Exception requestException)
        {
            try
            {
                BeginShutdown();
            }
            catch (Exception shutdownException)
            {
                throw new AggregateException("The dispatcher drain request and subsequent shutdown both failed.", requestException, shutdownException);
            }

            ExceptionDispatchInfo.Capture(requestException).Throw();
        }
    }

    private bool BeginShutdown()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return false;
        }

        _workQueue.Dispose();
        return true;
    }

    private void RunWithContext(Action action)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private T RunWithContext<T>(Func<T> action)
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
}