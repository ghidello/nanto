using System.Runtime.ExceptionServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting;

namespace Nanto.Testing;

public sealed class FakeNantoWindow : INantoWindow
{
    private static readonly Action<ILogger, Exception?> _rendererHandlerFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2, "RendererFailureHandlerFailed"),
        "A renderer-failure handler threw an exception.");
    private static readonly Action<ILogger, Exception?> _stateHandlerFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "WindowStateHandlerFailed"),
        "A window state-change handler threw an exception.");

    private readonly Lock _gate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly FailurePlan _failurePlan;
    private readonly WindowLifecycle _lifecycle;
    private readonly ILogger _logger;
    private readonly List<FakeWindowMutation> _mutations = [];
    private readonly TimeProvider _timeProvider;
    private Task? _closeTask;
    private WindowSnapshot _snapshot;

    public WindowId Id { get; }

    public string Title => Volatile.Read(ref _snapshot).Title;

    public WindowSize Size => Volatile.Read(ref _snapshot).Size;

    public WindowState State => _lifecycle.State;

    public bool IsVisible => Volatile.Read(ref _snapshot).IsVisible;

    public IReadOnlyList<FakeWindowMutation> Mutations
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly([.. _mutations]);
            }
        }
    }

    public event EventHandler<WindowStateChangedEventArgs>? StateChanged;

    public event EventHandler<RendererFailedEventArgs>? RendererFailed;

    public FakeNantoWindow(
        IUiDispatcher dispatcher,
        WindowOptions options,
        TimeProvider timeProvider,
        FailurePlan? failurePlan = null,
        WindowId? id = null,
        ILoggerFactory? loggerFactory = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentOutOfRangeException.ThrowIfEqual(options.InitialSize, default, nameof(options.InitialSize));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _failurePlan = failurePlan ?? new FailurePlan();
        _lifecycle = new WindowLifecycle(timeProvider);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FakeNantoWindow>();
        _snapshot = new WindowSnapshot(options.Title, options.InitialSize, options.StartVisible);
        if (id is { } specifiedId)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(specifiedId, default, nameof(id));
        }

        Id = id ?? WindowId.Create();
    }

    public ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return _dispatcher.InvokeAsync(
            () =>
            {
                ThrowIfClosing();
                _failurePlan.Observe("window.set-title");
                var snapshot = Volatile.Read(ref _snapshot);
                Volatile.Write(ref _snapshot, snapshot with { Title = title });
                Record(new FakeWindowMutation { Kind = FakeWindowMutationKind.TitleChanged, OccurredAt = _timeProvider.GetUtcNow(), Title = title });
            },
            cancellationToken);
    }

    public ValueTask SetSizeAsync(WindowSize size, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(size, default);
        return _dispatcher.InvokeAsync(
            () =>
            {
                ThrowIfClosing();
                _failurePlan.Observe("window.set-size");
                var snapshot = Volatile.Read(ref _snapshot);
                Volatile.Write(ref _snapshot, snapshot with { Size = size });
                Record(new FakeWindowMutation { Kind = FakeWindowMutationKind.SizeChanged, OccurredAt = _timeProvider.GetUtcNow(), Size = size });
            },
            cancellationToken);
    }

    public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => _dispatcher.InvokeAsync(
        () =>
        {
            ThrowIfClosing();
            _failurePlan.Observe("window.activate");
            Record(new FakeWindowMutation { Kind = FakeWindowMutationKind.Activated, OccurredAt = _timeProvider.GetUtcNow() });
        },
        cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        Task closeTask;
        TaskCompletionSource? closeCompletion = null;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                closeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeTask = closeCompletion.Task;
            }

            closeTask = _closeTask;
        }

        if (closeCompletion is not null)
        {
            _ = CompleteCloseAsync(closeCompletion);
        }

        return new ValueTask(closeTask.WaitAsync(cancellationToken));
    }

    public ValueTask RaiseRendererFailureAsync(RendererFailedEventArgs eventArgs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        return _dispatcher.InvokeAsync(() => RaiseRendererFailed(eventArgs), cancellationToken);
    }

    internal ValueTask StartAsync() => _dispatcher.InvokeAsync(
        () =>
        {
            TransitionTo(WindowState.Initializing);
            try
            {
                _failurePlan.Observe("window.initialize");
                TransitionTo(WindowState.Running);
            }
            catch (Exception exception)
            {
                TransitionTo(WindowState.Failed, exception);
                throw;
            }
        });

    private void CloseOnDispatcher()
    {
        if (State is WindowState.Closed or WindowState.Closing)
        {
            return;
        }

        Record(new FakeWindowMutation { Kind = FakeWindowMutationKind.CloseRequested, OccurredAt = _timeProvider.GetUtcNow() });
        Exception? failure = null;
        try
        {
            _failurePlan.Observe("window.close");
        }
        catch (Exception exception)
        {
            failure = exception;
            if (State != WindowState.Failed)
            {
                TransitionTo(WindowState.Failed, exception);
            }
        }

        TransitionTo(WindowState.Closing, failure);
        var snapshot = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, snapshot with { IsVisible = false });
        TransitionTo(WindowState.Closed, failure);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            await _dispatcher.InvokeAsync(CloseOnDispatcher, CancellationToken.None);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void TransitionTo(WindowState state, Exception? failure = null)
    {
        var eventArgs = _lifecycle.TransitionTo(state, failure);
        RaiseStateChanged(eventArgs);
    }

    private void RaiseStateChanged(WindowStateChangedEventArgs eventArgs)
    {
        foreach (EventHandler<WindowStateChangedEventArgs> handler in StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                _stateHandlerFailed(_logger, exception);
            }
        }
    }

    private void RaiseRendererFailed(RendererFailedEventArgs eventArgs)
    {
        foreach (EventHandler<RendererFailedEventArgs> handler in RendererFailed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                _rendererHandlerFailed(_logger, exception);
            }
        }
    }

    private void Record(FakeWindowMutation mutation)
    {
        lock (_gate)
        {
            _mutations.Add(mutation);
        }
    }

    private void ThrowIfClosing()
    {
        ObjectDisposedException.ThrowIf(State is WindowState.Closing or WindowState.Closed or WindowState.Failed, this);
    }

    private sealed record WindowSnapshot(string Title, WindowSize Size, bool IsVisible);
}
