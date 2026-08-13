using System.Collections.ObjectModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting;

namespace Nanto.Hosting.Windows;

public sealed class WindowsApplicationHost : INantoApplicationHost
{
    private static readonly Action<ILogger, Exception?> _stateHandlerFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, "ApplicationStateHandlerFailed"),
        "An application state-change handler threw an exception.");
    private static readonly Action<ILogger, Exception?> _timedOutTeardownFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2, "TimedOutTeardownFailed"),
        "Windows host teardown failed after the shutdown deadline had already expired.");

    private readonly IPhase1FailureInjector _failureInjector;
    private readonly Lock _gate = new();
    private readonly ApplicationLifecycle _lifecycle;
    private readonly ResourceLedger _resourceLedger;
    private readonly TaskCompletionSource _applicationLifetimeCancellationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _teardownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _timeProvider;
    private readonly IWindowsWebViewApplicationFactory _webViewApplicationFactory;
    private readonly CancellationTokenSource _applicationLifetime = new();
    private Exception? _applicationLifetimeCancellationFailure;
    private WindowsUiDispatcher? _dispatcher;
    private bool _disposeRequested;
    private ILogger _logger = NullLogger.Instance;
    private WindowsWindow? _ownedWindow;
    private INantoWindow? _primaryWindow;
    private int _preferredColorScheme;
    private bool _runClaimed;
    private bool _shutdownDeadlineTimedOut;
    private int _shutdownDeadlineStarted;
    private ShutdownMode _shutdownMode;
    private TimeSpan _shutdownTimeout;
    private Win32WindowClass? _windowClass;
    private WindowRegistry? _windowRegistry;
    private IWindowsWebViewApplication? _webViewApplication;

    public ApplicationState State => _lifecycle.State;

    public IUiDispatcher Dispatcher => Volatile.Read(ref _dispatcher)
        ?? throw new InvalidOperationException("The dispatcher is not available before RunAsync starts the Windows host.");

    public INantoWindow? PrimaryWindow => Volatile.Read(ref _primaryWindow);

    public ColorSchemePreference PreferredColorScheme => (ColorSchemePreference)Volatile.Read(ref _preferredColorScheme);

    internal ResourceLedgerSnapshot ResourceSnapshot => _resourceLedger.CaptureSnapshot();

    internal Task TeardownCompletion => _teardownCompletion.Task;

    public event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;

    public WindowsApplicationHost()
        : this(TimeProvider.System, NoOpPhase1FailureInjector.Instance, ProductionWindowsWebViewApplicationFactory.Instance)
    {
    }

    internal WindowsApplicationHost(
        TimeProvider timeProvider,
        IPhase1FailureInjector? failureInjector = null,
        IWindowsWebViewApplicationFactory? webViewApplicationFactory = null,
        bool captureResourceOwnershipEvents = false)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lifecycle = new ApplicationLifecycle(timeProvider);
        _failureInjector = failureInjector ?? NoOpPhase1FailureInjector.Instance;
        _webViewApplicationFactory = webViewApplicationFactory ?? ProductionWindowsWebViewApplicationFactory.Instance;
        _resourceLedger = new ResourceLedger(captureResourceOwnershipEvents);
    }

    public Task RunAsync(NantoApplicationOptions options, CancellationToken cancellationToken = default)
    {
        var validatedOptions = ValidatedApplicationOptions.Create(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_runClaimed)
            {
                throw new InvalidOperationException("RunAsync can be called only once.");
            }

            _logger = validatedOptions.LoggerFactory.CreateLogger<WindowsApplicationHost>();
            Volatile.Write(ref _preferredColorScheme, (int)validatedOptions.PreferredColorScheme);
            _shutdownTimeout = validatedOptions.ShutdownTimeout;
            _runClaimed = true;
        }

        _ = RunCoreAsync(validatedOptions, cancellationToken);
        return _runCompletion.Task;
    }

    public ValueTask SetPreferredColorSchemeAsync(
        ColorSchemePreference preferredColorScheme,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preferredColorScheme))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredColorScheme), preferredColorScheme, "The preferred color scheme is not supported.");
        }

        var state = State;
        if (state is ApplicationState.NotStarted or ApplicationState.Creating)
        {
            throw new InvalidOperationException("The preferred color scheme cannot be changed before the application is created.");
        }

        ObjectDisposedException.ThrowIf(
            _stopRequested.Task.IsCompleted || state is ApplicationState.Failed or ApplicationState.Closing or ApplicationState.Closed,
            this);
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The dispatcher is unavailable.");
        return dispatcher.InvokeAsync(
            async token =>
            {
                ObjectDisposedException.ThrowIf(_stopRequested.Task.IsCompleted, this);
                if (PreferredColorScheme == preferredColorScheme)
                {
                    return;
                }

                var webViewApplication = _webViewApplication
                    ?? throw new InvalidOperationException("The WebView2 application is unavailable.");
                await webViewApplication.SetPreferredColorSchemeAsync(preferredColorScheme, token);
                Volatile.Write(ref _preferredColorScheme, (int)preferredColorScheme);
            },
            cancellationToken);
    }

    internal ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken = default)
    {
        var webViewApplication = _webViewApplication
            ?? throw new InvalidOperationException("The WebView2 application is not available.");
        return webViewApplication.WaitForDiagnosticMessageAsync(cancellationToken);
    }

    internal ValueTask WaitForWebViewReadinessAsync(CancellationToken cancellationToken = default)
    {
        var webViewApplication = _webViewApplication
            ?? throw new InvalidOperationException("The WebView2 application is not available.");
        return webViewApplication.WaitForReadinessAsync(cancellationToken);
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
        var operation = "application.start";
        IReadOnlyList<Exception> cleanupExceptions = [];
        IDisposable? hostLease = null;
        WindowsUiThread? uiThread = null;
        var startupCancellationToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : _applicationLifetime.Token;
        using var cancellationRegistration = cancellationToken.UnsafeRegister(static state => ((WindowsApplicationHost)state!).RequestStop(), this);

        try
        {
            hostLease = _resourceLedger.Acquire(WindowsResourceKind.ApplicationHost, "ApplicationHost");
            _failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.ApplicationHostStarted);
            startupCancellationToken.ThrowIfCancellationRequested();

            uiThread = new WindowsUiThread(_resourceLedger, _failureInjector, startupCancellationToken);
            var dispatcher = await uiThread.DispatcherReady.ConfigureAwait(false);
            Volatile.Write(ref _dispatcher, dispatcher);
            _windowRegistry = new WindowRegistry(dispatcher);

            await dispatcher.InvokeAsync(() => TransitionTo(ApplicationState.Creating), CancellationToken.None).ConfigureAwait(false);
            if (!_stopRequested.Task.IsCompleted)
            {
                operation = "window.create";
                await CreatePrimaryWindowAsync(dispatcher, options).ConfigureAwait(false);
            }

            if (!_stopRequested.Task.IsCompleted)
            {
                operation = "application.activate";
                await dispatcher.InvokeAsync(() => TransitionTo(ApplicationState.Activated), CancellationToken.None).ConfigureAwait(false);
            }

            if (State == ApplicationState.Activated)
            {
                await _stopRequested.Task.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (_stopRequested.Task.IsCompleted)
        {
            await _applicationLifetimeCancellationCompletion.Task.ConfigureAwait(false);
        }

        Exception? applicationLifetimeCancellationFailure;
        lock (_gate)
        {
            applicationLifetimeCancellationFailure = _applicationLifetimeCancellationFailure;
        }

        if (applicationLifetimeCancellationFailure is not null)
        {
            if (primaryFailure is null)
            {
                primaryFailure = applicationLifetimeCancellationFailure;
                failureStage = NantoFailureStage.Teardown;
                operation = "application.cancel-lifetime";
            }
            else
            {
                cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, applicationLifetimeCancellationFailure, primaryFailure);
            }
        }

        var teardown = CompleteTeardownAsync(primaryFailure, failureStage, operation, cleanupExceptions, uiThread, hostLease);
        ShutdownResult shutdownResult;
        try
        {
            shutdownResult = await teardown.WaitAsync(options.ShutdownTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException timeoutException)
        {
            _ = ObserveTimedOutTeardownAsync(teardown, primaryFailure);
            if (primaryFailure is null)
            {
                primaryFailure = new TimeoutException($"Windows host teardown exceeded the configured timeout of {options.ShutdownTimeout}.", timeoutException);
                failureStage = NantoFailureStage.Teardown;
                operation = "application.shutdown-timeout";
            }
            else
            {
                cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, timeoutException, primaryFailure);
            }

            CompleteRun(primaryFailure, failureStage, operation, cleanupExceptions);
            return;
        }
        catch (Exception exception)
        {
            if (primaryFailure is null)
            {
                primaryFailure = exception;
                failureStage = NantoFailureStage.Teardown;
                operation = "application.close";
            }
            else
            {
                cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, exception, primaryFailure);
            }

            CompleteRun(primaryFailure, failureStage, operation, cleanupExceptions);
            return;
        }

        CompleteRun(shutdownResult.PrimaryFailure, shutdownResult.FailureStage, shutdownResult.Operation, shutdownResult.CleanupExceptions);
    }

    private static ReadOnlyCollection<Exception> AppendCleanupExceptions(
        IReadOnlyList<Exception> cleanupExceptions,
        Exception exception,
        Exception? primaryFailure = null)
    {
        IReadOnlyList<Exception> distinctExceptions = exception is AggregateException aggregateException
            ? aggregateException.Flatten().InnerExceptions
            : [exception];
        return Array.AsReadOnly(
        [
            .. cleanupExceptions,
            .. distinctExceptions.Where(candidate => !ReferenceEquals(candidate, primaryFailure)
                && !cleanupExceptions.Any(existing => ReferenceEquals(existing, candidate))),
        ]);
    }

    private async Task<ShutdownResult> CompleteTeardownAsync(
        Exception? primaryFailure,
        NantoFailureStage failureStage,
        string operation,
        IReadOnlyList<Exception> initialCleanupExceptions,
        WindowsUiThread? uiThread,
        IDisposable? hostLease)
    {
        var cleanupExceptions = initialCleanupExceptions;
        try
        {
            try
            {
                await TransitionForShutdownAsync(primaryFailure).ConfigureAwait(false);
                var nativeCleanupExceptions = await CleanupNativeResourcesAsync().ConfigureAwait(false);
                foreach (var cleanupException in nativeCleanupExceptions)
                {
                    cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, cleanupException, primaryFailure);
                }

                if (primaryFailure is null && cleanupExceptions.Count > 0)
                {
                    primaryFailure = cleanupExceptions[0].InnerException ?? cleanupExceptions[0];
                    cleanupExceptions = cleanupExceptions.Count == 1 ? [] : Array.AsReadOnly([.. cleanupExceptions.Skip(1)]);
                    failureStage = NantoFailureStage.Teardown;
                    operation = "application.close";
                }

                await TransitionToClosedAsync(primaryFailure).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = exception;
                    failureStage = NantoFailureStage.Teardown;
                    operation = "application.close";
                }
                else
                {
                    cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, exception, primaryFailure);
                }
            }

            if (uiThread is not null)
            {
                try
                {
                    await uiThread.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = exception;
                        failureStage = NantoFailureStage.Teardown;
                        operation = "ui-thread.stop";
                    }
                    else
                    {
                        cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, exception, primaryFailure);
                    }
                }
            }

            try
            {
                hostLease?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, exception, primaryFailure);
            }

            if (primaryFailure is null && cleanupExceptions.Count > 0)
            {
                primaryFailure = cleanupExceptions[0].InnerException ?? cleanupExceptions[0];
                cleanupExceptions = cleanupExceptions.Count == 1 ? [] : Array.AsReadOnly([.. cleanupExceptions.Skip(1)]);
                failureStage = NantoFailureStage.Teardown;
                operation = "application.close";
            }

            _teardownCompletion.TrySetResult();
            return new ShutdownResult(primaryFailure, failureStage, operation, cleanupExceptions);
        }
        catch
        {
            _teardownCompletion.TrySetResult();
            throw;
        }
    }

    private void CompleteRun(
        Exception? primaryFailure,
        NantoFailureStage failureStage,
        string operation,
        IReadOnlyList<Exception> cleanupExceptions)
    {
        var completed = false;
        var shutdownDeadlineTimedOut = false;
        if (primaryFailure is null)
        {
            lock (_gate)
            {
                completed = _runCompletion.TrySetResult();
                shutdownDeadlineTimedOut = _shutdownDeadlineTimedOut;
            }
        }
        else
        {
            var hostException = new NantoHostException("The Windows Nanto application host failed.", primaryFailure)
            {
                Stage = failureStage,
                Operation = operation,
                CleanupExceptions = cleanupExceptions,
            };
            lock (_gate)
            {
                completed = _runCompletion.TrySetException(hostException);
                shutdownDeadlineTimedOut = _shutdownDeadlineTimedOut;
            }
        }

        if (!completed && shutdownDeadlineTimedOut)
        {
            ReportFailureAfterShutdownDeadline(primaryFailure, operation, cleanupExceptions);
        }
    }

    private void ReportFailureAfterShutdownDeadline(
        Exception? primaryFailure,
        string operation,
        IReadOnlyList<Exception> cleanupExceptions)
    {
        List<Exception>? laterFailures = null;
        if (primaryFailure is not null
            && primaryFailure is not OperationCanceledException
            && primaryFailure is not TimeoutException
            && !string.Equals(operation, "application.shutdown-timeout", StringComparison.Ordinal))
        {
            laterFailures = [primaryFailure];
        }

        if (cleanupExceptions.Count > 0)
        {
            laterFailures ??= [];
            laterFailures.AddRange(cleanupExceptions);
        }

        if (laterFailures is [var laterFailure])
        {
            _timedOutTeardownFailed(_logger, laterFailure);
        }
        else if (laterFailures is { Count: > 1 })
        {
            _timedOutTeardownFailed(_logger, new AggregateException("Windows host teardown encountered failures after timing out.", laterFailures));
        }
    }

    private async Task ObserveTimedOutTeardownAsync(Task<ShutdownResult> teardown, Exception? originalPrimaryFailure)
    {
        try
        {
            var result = await teardown.ConfigureAwait(false);
            List<Exception>? laterFailures = null;
            if (result.PrimaryFailure is not null && !ReferenceEquals(result.PrimaryFailure, originalPrimaryFailure))
            {
                laterFailures = [result.PrimaryFailure];
            }

            if (result.CleanupExceptions.Count > 0)
            {
                laterFailures ??= [];
                laterFailures.AddRange(result.CleanupExceptions);
            }

            if (laterFailures is [var laterFailure])
            {
                _timedOutTeardownFailed(_logger, laterFailure);
            }
            else if (laterFailures is { Count: > 1 })
            {
                _timedOutTeardownFailed(_logger, new AggregateException("Windows host teardown encountered failures after timing out.", laterFailures));
            }
        }
        catch (Exception exception)
        {
            _timedOutTeardownFailed(_logger, exception);
        }
    }

    private async ValueTask CreatePrimaryWindowAsync(WindowsUiDispatcher dispatcher, ValidatedApplicationOptions options)
    {
        await dispatcher.InvokeAsync(
            async _ =>
            {
                Win32WindowClass? windowClass = null;
                WindowsWindow? window = null;
                IWindowsWebViewApplication? webViewApplication = null;
                try
                {
                    webViewApplication = await _webViewApplicationFactory.CreateAsync(
                        options,
                        dispatcher,
                        _resourceLedger,
                        _failureInjector,
                        _applicationLifetime.Token);
                    windowClass = new Win32WindowClass(_resourceLedger, dispatcher, _failureInjector);
                    _applicationLifetime.Token.ThrowIfCancellationRequested();
                    window = await WindowsWindow.CreateAsync(
                        windowClass,
                        _resourceLedger,
                        dispatcher,
                        webViewApplication,
                        options.PrimaryWindow,
                        options.PreferredColorScheme,
                        _failureInjector,
                        _timeProvider,
                        loggerFactory: options.LoggerFactory,
                        cancellationToken: _applicationLifetime.Token);
                    window.StateChanged += HandleWindowStateChanged;
                    _windowRegistry!.Add(window);
                    _shutdownMode = options.ShutdownMode;
                    Volatile.Write(ref _primaryWindow, window);
                    _ownedWindow = window;
                    _windowClass = windowClass;
                    _webViewApplication = webViewApplication;
                    TransitionTo(ApplicationState.Created);
                }
                catch (Exception creationException)
                {
                    List<Exception>? cleanupExceptions = null;
                    if (window is not null)
                    {
                        try
                        {
                            await window.DisposeAsync();
                        }
                        catch (Exception exception)
                        {
                            cleanupExceptions ??= [];
                            cleanupExceptions.Add(exception);
                        }
                    }

                    try
                    {
                        windowClass?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        cleanupExceptions ??= [];
                        cleanupExceptions.Add(exception);
                    }

                    if (webViewApplication is not null)
                    {
                        try
                        {
                            await webViewApplication.DisposeAsync();
                        }
                        catch (Exception exception)
                        {
                            cleanupExceptions ??= [];
                            cleanupExceptions.Add(exception);
                        }
                    }

                    if (cleanupExceptions is not null)
                    {
                        throw new AggregateException("Windows window creation failed and cleanup also failed.", [creationException, .. cleanupExceptions]);
                    }

                    throw;
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<Exception>> CleanupNativeResourcesAsync()
    {
        var cleanup = new AsyncCleanupRegistry();
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            return [];
        }

        var webViewApplication = _webViewApplication;
        if (webViewApplication is not null)
        {
            cleanup.Push(
                "webview2.application.close",
                () => dispatcher.InvokeAsync(_ => webViewApplication.DisposeAsync(), CancellationToken.None));
        }

        var windowClass = _windowClass;
        if (windowClass is not null)
        {
            cleanup.Push("window-class.unregister", () => dispatcher.InvokeAsync(windowClass.Dispose, CancellationToken.None));
        }

        var window = _ownedWindow;
        if (window is not null)
        {
            // DisposeAsync is also the authoritative observation point for a close that
            // the native window initiated and may already have completed with a failure.
            cleanup.Push("window.close", () => window.DisposeAsync());

            cleanup.Push(
                "window.unpublish",
                () => dispatcher.InvokeAsync(
                    () =>
                    {
                        window.StateChanged -= HandleWindowStateChanged;
                        _windowRegistry?.Remove(window);
                        Volatile.Write(ref _primaryWindow, null);
                    },
                    CancellationToken.None));
        }

        var failures = await cleanup.DrainAsync().ConfigureAwait(false);
        _ownedWindow = null;
        _windowClass = null;
        _webViewApplication = null;
        return failures;
    }

    private void HandleWindowStateChanged(object? sender, WindowStateChangedEventArgs eventArgs)
    {
        if (sender is not WindowsWindow window)
        {
            return;
        }

        if (eventArgs.NewState == WindowState.Closing)
        {
            if (_shutdownMode != ShutdownMode.Explicit)
            {
                RequestStop();
            }

            return;
        }

        if (eventArgs.NewState != WindowState.Closed)
        {
            return;
        }

        _windowRegistry?.Remove(window);
        Volatile.Write(ref _primaryWindow, null);
        if (_shutdownMode != ShutdownMode.Explicit)
        {
            RequestStop();
        }
    }

    private void RequestStop()
    {
        _stopRequested.TrySetResult();
        if (Interlocked.Exchange(ref _shutdownDeadlineStarted, 1) != 0)
        {
            return;
        }

        _ = EnforceShutdownDeadlineAsync();
        _ = CancelApplicationLifetimeAsync();
    }

    private async Task CancelApplicationLifetimeAsync()
    {
        try
        {
            await _applicationLifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _applicationLifetimeCancellationFailure = exception;
            }
        }
        finally
        {
            _applicationLifetimeCancellationCompletion.TrySetResult();
        }
    }

    private async Task EnforceShutdownDeadlineAsync()
    {
        try
        {
            await _teardownCompletion.Task.WaitAsync(_shutdownTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException timeoutException)
        {
            var hostException = new NantoHostException(
                $"Windows host teardown exceeded the configured timeout of {_shutdownTimeout}.",
                timeoutException)
            {
                Stage = NantoFailureStage.Teardown,
                Operation = "application.shutdown-timeout",
            };
            lock (_gate)
            {
                if (_runCompletion.Task.IsCompleted)
                {
                    return;
                }

                _shutdownDeadlineTimedOut = true;
                _runCompletion.TrySetException(hostException);
            }
        }
    }

    private async ValueTask TransitionForShutdownAsync(Exception? failure)
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            if (failure is not null && State != ApplicationState.Failed)
            {
                TransitionTo(ApplicationState.Failed, failure);
            }

            if (State != ApplicationState.Closing)
            {
                TransitionTo(ApplicationState.Closing, failure);
            }

            return;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                if (failure is not null && State != ApplicationState.Failed)
                {
                    TransitionTo(ApplicationState.Failed, failure);
                }

                TransitionTo(ApplicationState.Closing, failure);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask TransitionToClosedAsync(Exception? failure)
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            TransitionTo(ApplicationState.Closed, failure);
            return;
        }

        await dispatcher.InvokeAsync(() => TransitionTo(ApplicationState.Closed, failure), CancellationToken.None).ConfigureAwait(false);
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

    private sealed record ShutdownResult(
        Exception? PrimaryFailure,
        NantoFailureStage FailureStage,
        string Operation,
        IReadOnlyList<Exception> CleanupExceptions);
}