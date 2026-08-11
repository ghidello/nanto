using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting;

namespace Nanto.Testing;

public sealed class FakeNantoApplicationHost : INantoApplicationHost
{
    private static readonly Action<ILogger, Exception?> _stateHandlerFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "ApplicationStateHandlerFailed"),
        "An application state-change handler threw an exception.");

    private readonly Lock _gate = new();
    private readonly ManualUiDispatcher _dispatcher;
    private readonly FailurePlan _failurePlan;
    private readonly ApplicationLifecycle _lifecycle;
    private readonly TaskCompletionSource _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _timeProvider;
    private bool _disposeRequested;
    private bool _runClaimed;
    private ILogger _logger = NullLogger.Instance;
    private FakeNantoWindow? _ownedWindow;
    private INantoWindow? _primaryWindow;
    private ShutdownMode _shutdownMode;
    private Exception? _windowFailure;

    public ApplicationState State => _lifecycle.State;

    public IUiDispatcher Dispatcher
    {
        get
        {
            lock (_gate)
            {
                if (!_runClaimed)
                {
                    throw new InvalidOperationException("The dispatcher is not available before RunAsync claims the host.");
                }
            }

            return _dispatcher;
        }
    }

    public INantoWindow? PrimaryWindow => Volatile.Read(ref _primaryWindow);

    public ManualAsyncGate CreationGate { get; } = new();

    public ManualAsyncGate ActivationGate { get; } = new();

    public ManualAsyncGate FailureGate { get; } = new();

    public ManualAsyncGate StopGate { get; } = new();

    public ManualAsyncGate CloseGate { get; } = new();

    public event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;

    public FakeNantoApplicationHost(ManualUiDispatcher dispatcher, TimeProvider timeProvider, FailurePlan? failurePlan = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _failurePlan = failurePlan ?? new FailurePlan();
        _lifecycle = new ApplicationLifecycle(timeProvider);
    }

    public Task RunAsync(NantoApplicationOptions options, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
        }

        var validatedOptions = ValidatedApplicationOptions.Create(options);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_runClaimed)
            {
                throw new InvalidOperationException("RunAsync can be called only once.");
            }

            _logger = validatedOptions.LoggerFactory.CreateLogger<FakeNantoApplicationHost>();
            _runClaimed = true;
        }

        _ = RunCoreAsync(validatedOptions, cancellationToken);
        return _runCompletion.Task;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_runClaimed)
            {
                return ValueTask.CompletedTask;
            }
        }

        RequestStop();
        return new ValueTask(_runCompletion.Task.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return _runClaimed ? new ValueTask(_runCompletion.Task) : ValueTask.CompletedTask;
            }

            _disposeRequested = true;
            if (!_runClaimed)
            {
                return ValueTask.CompletedTask;
            }
        }

        RequestStop();
        return new ValueTask(_runCompletion.Task);
    }

    private async Task RunCoreAsync(ValidatedApplicationOptions options, CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        var failureStage = NantoFailureStage.Startup;
        var operation = "application.create";
        IReadOnlyList<Exception> cleanupExceptions = [];
        using var cancellationRegistration = cancellationToken.UnsafeRegister(static state => ((FakeNantoApplicationHost)state!).RequestStop(), this);

        try
        {
            await DispatchAsync(() => TransitionTo(ApplicationState.Creating)).ConfigureAwait(false);

            if (!_stopRequested.Task.IsCompleted)
            {
                await WaitForGateOrStopAsync(CreationGate).ConfigureAwait(false);
            }

            if (!_stopRequested.Task.IsCompleted)
            {
                operation = "application.create";
                await DispatchAsync(() => CreatePrimaryWindowAsync(options)).ConfigureAwait(false);
            }

            if (!_stopRequested.Task.IsCompleted)
            {
                await WaitForGateOrStopAsync(ActivationGate).ConfigureAwait(false);
            }

            if (!_stopRequested.Task.IsCompleted)
            {
                operation = "application.activate";
                await DispatchAsync(
                    () =>
                    {
                        _failurePlan.Observe(operation);
                        TransitionTo(ApplicationState.Activated);
                    }).ConfigureAwait(false);
            }

            if (State == ApplicationState.Activated)
            {
                await _stopRequested.Task.ConfigureAwait(false);
            }

            await StopGate.WaitAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_windowFailure is not null)
                {
                    primaryFailure = _windowFailure;
                    failureStage = NantoFailureStage.Runtime;
                    operation = "window.close";
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            operation = _failurePlan.LastObservedOperation ?? operation;
            await DispatchAsync(() => TransitionTo(ApplicationState.Failed, exception)).ConfigureAwait(false);
            await FailureGate.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            if (State != ApplicationState.Failed && primaryFailure is not null)
            {
                await DispatchAsync(() => TransitionTo(ApplicationState.Failed, primaryFailure)).ConfigureAwait(false);
                await FailureGate.WaitAsync().ConfigureAwait(false);
            }

            await DispatchAsync(() => TransitionTo(ApplicationState.Closing, primaryFailure)).ConfigureAwait(false);
            await CloseGate.WaitAsync().ConfigureAwait(false);

            var cleanup = new AsyncCleanupRegistry();
            var window = _ownedWindow;
            if (window is not null)
            {
                if (window.State != WindowState.Closed)
                {
                    cleanup.Push("window.close", () => window.CloseAsync(CancellationToken.None));
                }

                cleanup.Push(
                    "window.state-subscription",
                    () =>
                    {
                        window.StateChanged -= HandleWindowStateChanged;
                        return ValueTask.CompletedTask;
                    });
            }

            cleanupExceptions = await cleanup.DrainAsync().ConfigureAwait(false);
            _ownedWindow = null;
            Volatile.Write(ref _primaryWindow, null);

            if (primaryFailure is null && cleanupExceptions.Count > 0)
            {
                primaryFailure = cleanupExceptions[0].InnerException ?? cleanupExceptions[0];
                cleanupExceptions = cleanupExceptions.Count == 1 ? [] : Array.AsReadOnly([.. cleanupExceptions.Skip(1)]);
                failureStage = NantoFailureStage.Teardown;
                operation = "window.close";
            }

            await DispatchAsync(() => TransitionTo(ApplicationState.Closed, primaryFailure)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure ??= exception;
            failureStage = NantoFailureStage.Teardown;
            operation = "application.close";
        }
        finally
        {
            _dispatcher.Dispose();
        }

        if (primaryFailure is null)
        {
            _runCompletion.TrySetResult();
            return;
        }

        _runCompletion.TrySetException(
            new NantoHostException("The fake Nanto application host failed.", primaryFailure)
            {
                Stage = failureStage,
                Operation = operation,
                CleanupExceptions = cleanupExceptions,
            });
    }

    private async ValueTask CreatePrimaryWindowAsync(ValidatedApplicationOptions options)
    {
        _failurePlan.Observe("application.create");
        var window = new FakeNantoWindow(
            _dispatcher,
            options.PrimaryWindow,
            _timeProvider,
            _failurePlan,
            loggerFactory: options.LoggerFactory);
        _shutdownMode = options.ShutdownMode;
        _ownedWindow = window;
        window.StateChanged += HandleWindowStateChanged;
        Volatile.Write(ref _primaryWindow, window);
        await window.StartAsync();
        TransitionTo(ApplicationState.Created);
    }

    private ValueTask DispatchAsync(Action action) => _dispatcher.InvokeAsync(action, CancellationToken.None);

    private ValueTask DispatchAsync(Func<ValueTask> action) => _dispatcher.InvokeAsync(_ => action(), CancellationToken.None);

    private void HandleWindowStateChanged(object? sender, WindowStateChangedEventArgs eventArgs)
    {
        if (eventArgs.NewState != WindowState.Closed)
        {
            return;
        }

        if (eventArgs.Failure is not null)
        {
            lock (_gate)
            {
                _windowFailure ??= eventArgs.Failure;
            }
        }

        Volatile.Write(ref _primaryWindow, null);
        if (_shutdownMode != ShutdownMode.Explicit)
        {
            RequestStop();
        }
    }

    private void RequestStop() => _stopRequested.TrySetResult();

    private async Task WaitForGateOrStopAsync(ManualAsyncGate gate)
    {
        var gateTask = gate.WaitAsync();
        await Task.WhenAny(gateTask, _stopRequested.Task).ConfigureAwait(false);
    }

    private void TransitionTo(ApplicationState state, Exception? failure = null)
    {
        var eventArgs = _lifecycle.TransitionTo(state, failure);
        foreach (EventHandler<ApplicationStateChangedEventArgs> handler in StateChanged?.GetInvocationList() ?? [])
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
}
