using System.Collections.ObjectModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting;

namespace Nanto.Hosting.Windows;

public sealed class WindowsApplicationHost : INantoApplicationHost
{
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
    private int _runCompletionClaimed;
    private long _runStartedAt;
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
            _runStartedAt = _timeProvider.GetTimestamp();
            _runClaimed = true;
        }

        WindowsDiagnostics.ApplicationStarting(
            _logger,
            validatedOptions.Identity.StorageKey[^32..],
            validatedOptions.ShutdownMode,
            validatedOptions.ShutdownTimeout.TotalMilliseconds);

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

    internal ValueTask CrashRendererForTestingAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The dispatcher is unavailable.");
        return dispatcher.InvokeAsync(
            () =>
            {
                var webViewApplication = _webViewApplication
                    ?? throw new InvalidOperationException("The WebView2 application is unavailable.");
                webViewApplication.CrashRendererForTesting();
            },
            cancellationToken);
    }

    internal ValueTask<uint> GetBrowserProcessIdForTestingAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The dispatcher is unavailable.");
        return dispatcher.InvokeAsync(
            () =>
            {
                var webViewApplication = _webViewApplication
                    ?? throw new InvalidOperationException("The WebView2 application is unavailable.");
                return webViewApplication.GetBrowserProcessIdForTesting();
            },
            cancellationToken);
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

        RequestStop(ShutdownTrigger.StopRequested);
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

        RequestStop(ShutdownTrigger.DisposeRequested);
        return new ValueTask(_runCompletion.Task);
    }

    private async Task RunCoreAsync(ValidatedApplicationOptions options, CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        var failureStage = NantoFailureStage.Startup;
        var operation = "application.start";
        ReadOnlyCollection<Exception> cleanupExceptions = Array.AsReadOnly<Exception>([]);
        IDisposable? hostLease = null;
        WindowsUiThread? uiThread = null;
        var startupCancellationToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : _applicationLifetime.Token;
        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((WindowsApplicationHost)state!).RequestStop(ShutdownTrigger.RunCanceled),
            this);

        try
        {
            hostLease = _resourceLedger.Acquire(WindowsResourceKind.ApplicationHost, "ApplicationHost");
            _failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.ApplicationHostStarted);
            startupCancellationToken.ThrowIfCancellationRequested();

            uiThread = new WindowsUiThread(
                _resourceLedger,
                _failureInjector,
                options.LoggerFactory,
                _timeProvider,
                startupCancellationToken);
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
            RequestStop(ShutdownTrigger.StartupFailure);
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

            CompleteRun(primaryFailure, failureStage, operation, cleanupExceptions, cleanupExceptions.Count);
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

            CompleteRun(primaryFailure, failureStage, operation, cleanupExceptions, cleanupExceptions.Count);
            return;
        }

        CompleteRun(
            shutdownResult.PrimaryFailure,
            shutdownResult.FailureStage,
            shutdownResult.Operation,
            shutdownResult.CleanupExceptions,
            shutdownResult.CleanupFailureCount);
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
        var teardownStartedAt = _timeProvider.GetTimestamp();
        WindowsDiagnostics.TeardownStarted(_logger);
        var cleanupExceptions = initialCleanupExceptions;
        var cleanupFailureCount = cleanupExceptions.Count;
        try
        {
            try
            {
                await TransitionForShutdownAsync(primaryFailure).ConfigureAwait(false);
                var nativeCleanupExceptions = await CleanupNativeResourcesAsync().ConfigureAwait(false);
                foreach (var cleanupException in nativeCleanupExceptions)
                {
                    cleanupFailureCount++;
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
                cleanupFailureCount++;
                WindowsDiagnostics.CleanupOperationFailed(
                    _logger,
                    "LifecycleTransition",
                    exception.GetType().Name,
                    exception.HResult);
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
                    cleanupFailureCount++;
                    WindowsDiagnostics.CleanupOperationFailed(
                        _logger,
                        "UiThread",
                        exception.GetType().Name,
                        exception.HResult);
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
                cleanupFailureCount++;
                WindowsDiagnostics.CleanupOperationFailed(
                    _logger,
                    "ApplicationHostLease",
                    exception.GetType().Name,
                    exception.HResult);
                cleanupExceptions = AppendCleanupExceptions(cleanupExceptions, exception, primaryFailure);
            }

            if (primaryFailure is null && cleanupExceptions.Count > 0)
            {
                primaryFailure = cleanupExceptions[0].InnerException ?? cleanupExceptions[0];
                cleanupExceptions = cleanupExceptions.Count == 1 ? [] : Array.AsReadOnly([.. cleanupExceptions.Skip(1)]);
                failureStage = NantoFailureStage.Teardown;
                operation = "application.close";
            }

            var snapshot = _resourceLedger.CaptureSnapshot();
            WindowsDiagnostics.TeardownCompleted(
                _logger,
                _timeProvider.GetElapsedTime(teardownStartedAt).TotalMilliseconds,
                cleanupFailureCount,
                snapshot.TotalActive);
            _teardownCompletion.TrySetResult();
            return new ShutdownResult(primaryFailure, failureStage, operation, cleanupExceptions, cleanupFailureCount);
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
        IReadOnlyList<Exception> cleanupExceptions,
        int cleanupFailureCount)
    {
        var completed = Interlocked.CompareExchange(ref _runCompletionClaimed, 1, 0) == 0;
        bool shutdownDeadlineTimedOut;
        if (completed)
        {
            try
            {
                if (primaryFailure is null)
                {
                    var snapshot = _resourceLedger.CaptureSnapshot();
                    WindowsDiagnostics.ApplicationStopped(
                        _logger,
                        _timeProvider.GetElapsedTime(_runStartedAt).TotalMilliseconds,
                        cleanupFailureCount,
                        snapshot.TotalActive);
                }
                else
                {
                    WindowsDiagnostics.ApplicationFailed(
                        _logger,
                        failureStage,
                        operation,
                        primaryFailure.GetType().Name,
                        primaryFailure.HResult,
                        _timeProvider.GetElapsedTime(_runStartedAt).TotalMilliseconds);
                }
            }
            finally
            {
                if (primaryFailure is null)
                {
                    _runCompletion.TrySetResult();
                }
                else
                {
                    var hostException = new NantoHostException("The Windows Nanto application host failed.", primaryFailure)
                    {
                        Stage = failureStage,
                        Operation = operation,
                        CleanupExceptions = cleanupExceptions,
                    };
                    _runCompletion.TrySetException(hostException);
                }
            }
        }

        lock (_gate)
        {
            shutdownDeadlineTimedOut = _shutdownDeadlineTimedOut;
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
            WindowsDiagnostics.TimedOutTeardownFailed(_logger, operation, laterFailure.GetType().Name, laterFailure.HResult);
        }
        else if (laterFailures is { Count: > 1 })
        {
            WindowsDiagnostics.TimedOutTeardownFailed(_logger, operation, nameof(AggregateException), 0);
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
                WindowsDiagnostics.TimedOutTeardownFailed(_logger, result.Operation, laterFailure.GetType().Name, laterFailure.HResult);
            }
            else if (laterFailures is { Count: > 1 })
            {
                WindowsDiagnostics.TimedOutTeardownFailed(_logger, result.Operation, nameof(AggregateException), 0);
            }
        }
        catch (Exception exception)
        {
            WindowsDiagnostics.TimedOutTeardownFailed(_logger, "ObserveTimedOutTeardown", exception.GetType().Name, exception.HResult);
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
                        _timeProvider,
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
            PushCleanup(
                cleanup,
                "webview2.application.close",
                () => dispatcher.InvokeAsync(_ => webViewApplication.DisposeAsync(), CancellationToken.None));
        }

        var windowClass = _windowClass;
        if (windowClass is not null)
        {
            PushCleanup(cleanup, "window-class.unregister", () => dispatcher.InvokeAsync(windowClass.Dispose, CancellationToken.None));
        }

        var window = _ownedWindow;
        if (window is not null)
        {
            // DisposeAsync is also the authoritative observation point for a close that
            // the native window initiated and may already have completed with a failure.
            PushCleanup(cleanup, "window.close", () => window.DisposeAsync());

            PushCleanup(
                cleanup,
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

    private void PushCleanup(AsyncCleanupRegistry registry, string operation, Func<ValueTask> cleanup)
    {
        registry.Push(
            operation,
            async () =>
            {
                try
                {
                    await cleanup().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    WindowsDiagnostics.CleanupOperationFailed(
                        _logger,
                        operation,
                        exception.GetType().Name,
                        exception.HResult);
                    throw;
                }
            });
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
                RequestStop(ShutdownTrigger.PrimaryWindowClosed);
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
            RequestStop(ShutdownTrigger.PrimaryWindowClosed);
        }
    }

    private void RequestStop(ShutdownTrigger trigger)
    {
        if (Interlocked.CompareExchange(ref _shutdownDeadlineStarted, 1, 0) != 0)
        {
            return;
        }

        WindowsDiagnostics.ShutdownRequested(_logger, trigger);
        _ = EnforceShutdownDeadlineAsync();
        _ = CancelApplicationLifetimeAsync();
        _stopRequested.TrySetResult();
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
            lock (_gate)
            {
                if (Interlocked.CompareExchange(ref _runCompletionClaimed, 1, 0) != 0)
                {
                    return;
                }

                _shutdownDeadlineTimedOut = true;
            }

            WindowsDiagnostics.ShutdownDeadlineExceeded(_logger, _shutdownTimeout.TotalMilliseconds);
            var hostException = new NantoHostException(
                $"Windows host teardown exceeded the configured timeout of {_shutdownTimeout}.",
                timeoutException)
            {
                Stage = NantoFailureStage.Teardown,
                Operation = "application.shutdown-timeout",
            };
            try
            {
                WindowsDiagnostics.ApplicationFailed(
                    _logger,
                    NantoFailureStage.Teardown,
                    "application.shutdown-timeout",
                    timeoutException.GetType().Name,
                    timeoutException.HResult,
                    _timeProvider.GetElapsedTime(_runStartedAt).TotalMilliseconds);
            }
            finally
            {
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
        var previousState = State;
        var eventArgs = _lifecycle.TransitionTo(state, failure);
        WindowsDiagnostics.ApplicationStateChanged(_logger, previousState, state);
        if (state == ApplicationState.Activated)
        {
            WindowsDiagnostics.ApplicationRunning(_logger, _timeProvider.GetElapsedTime(_runStartedAt).TotalMilliseconds);
        }

        foreach (EventHandler<ApplicationStateChangedEventArgs> handler in StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                WindowsDiagnostics.ApplicationStateHandlerFailed(_logger, exception);
            }
        }
    }

    private sealed record ShutdownResult(
        Exception? PrimaryFailure,
        NantoFailureStage FailureStage,
        string Operation,
        IReadOnlyList<Exception> CleanupExceptions,
        int CleanupFailureCount);
}
