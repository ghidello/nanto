using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32;
using Windows.Win32.System.WinRT;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows;

internal sealed class WindowsUiThread : IAsyncDisposable
{
    private const uint DrainDispatcherMessage = PInvoke.WM_APP;

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WindowsUiDispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IPhase1FailureInjector _failureInjector;
    private readonly CancellationToken _startupCancellationToken;
    private readonly Lock _failureGate = new();
    private readonly ILogger _logger;
    private readonly ResourceLedger _resourceLedger;
    private readonly Thread _thread;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedAt;
    private List<Exception>? _failures;
    private WindowsUiDispatcher? _dispatcher;
    private int _dispatcherShutdownCompleted;
    private int _dispatcherShutdownStarted;
    private uint _nativeThreadId;
    private int _stopRequested;

    public Task Completion => _completion.Task;

    public Task<WindowsUiDispatcher> DispatcherReady => _dispatcherReady.Task;

    public WindowsUiThread(
        ResourceLedger resourceLedger,
        IPhase1FailureInjector? failureInjector = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        CancellationToken startupCancellationToken = default)
    {
        _resourceLedger = resourceLedger ?? throw new ArgumentNullException(nameof(resourceLedger));
        _failureInjector = failureInjector ?? NoOpPhase1FailureInjector.Instance;
        _startupCancellationToken = startupCancellationToken;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsUiThread>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetTimestamp();
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Nanto UI",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        WindowsDiagnostics.UiThreadStarting(_logger);
        _thread.Start();
    }

    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        WindowsDiagnostics.UiThreadStopping(_logger);
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is not null)
        {
            ShutdownDispatcher(dispatcher);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher?.CheckAccess() == true)
        {
            throw new InvalidOperationException("The UI thread cannot asynchronously wait for its own message loop to stop.");
        }

        RequestStop();
        await _completion.Task.ConfigureAwait(false);
    }

    private void ThreadMain()
    {
        IDisposable? runtimeApartmentLease = null;
        IDisposable? threadLease = null;
        DPI_AWARENESS_CONTEXT previousDpiAwareness = default;
        var runtimeApartmentInitialized = false;
        try
        {
            previousDpiAwareness = PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previousDpiAwareness.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not enable Per-Monitor-V2 awareness on its UI thread.");
            }

            HResult.ThrowIfFailed(
                PInvoke.RoInitialize(RO_INIT_TYPE.RO_INIT_SINGLETHREADED),
                "winrt.apartment.initialize",
                NantoFailureStage.Startup);
            runtimeApartmentInitialized = true;
            runtimeApartmentLease = _resourceLedger.Acquire(WindowsResourceKind.WindowsRuntimeApartment, "WindowsRuntimeApartment");
            _failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WindowsRuntimeApartmentInitialized);
            _startupCancellationToken.ThrowIfCancellationRequested();

            threadLease = _resourceLedger.Acquire(WindowsResourceKind.UiThread, "UiThread");
            _failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.UiThreadStarted);
            _startupCancellationToken.ThrowIfCancellationRequested();
            _nativeThreadId = PInvoke.GetCurrentThreadId();
            _ = PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
            _failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.NativeMessageQueueCreated);
            _startupCancellationToken.ThrowIfCancellationRequested();

            var managedThreadId = Environment.CurrentManagedThreadId;
            var dispatcher = new WindowsUiDispatcher(
                _resourceLedger,
                () => Environment.CurrentManagedThreadId == managedThreadId,
                RequestDispatcherDrain);
            Volatile.Write(ref _dispatcher, dispatcher);
            _failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.DispatcherCreated);
            _startupCancellationToken.ThrowIfCancellationRequested();
            WindowsDiagnostics.UiThreadReady(
                _logger,
                _nativeThreadId,
                _timeProvider.GetElapsedTime(_startedAt).TotalMilliseconds);
            _dispatcherReady.TrySetResult(dispatcher);

            if (Volatile.Read(ref _stopRequested) != 0)
            {
                ShutdownDispatcher(dispatcher);
            }

            RunMessageLoop(dispatcher);
        }
        catch (Exception exception)
        {
            AddFailure(exception, "ThreadMain");
            _dispatcherReady.TrySetException(exception);
        }
        finally
        {
            var dispatcher = Volatile.Read(ref _dispatcher);
            if (dispatcher is not null)
            {
                ShutdownDispatcher(dispatcher);
            }

            try
            {
                threadLease?.Dispose();
            }
            catch (Exception exception)
            {
                AddFailure(exception, "ReleaseThreadLease");
            }

            if (!previousDpiAwareness.IsNull && PInvoke.SetThreadDpiAwarenessContext(previousDpiAwareness).IsNull)
            {
                AddFailure(
                    new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not restore the UI thread DPI-awareness context."),
                    "RestoreDpiAwareness");
            }

            if (runtimeApartmentInitialized)
            {
                PInvoke.RoUninitialize();
            }

            try
            {
                runtimeApartmentLease?.Dispose();
            }
            catch (Exception exception)
            {
                AddFailure(exception, "ReleaseWindowsRuntimeApartmentLease");
            }

            CompleteThread();
        }
    }

    private void RunMessageLoop(WindowsUiDispatcher dispatcher)
    {
        while (true)
        {
            var result = PInvoke.GetMessage(out var message, default, 0, 0);
            if (result.Value == -1)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Nanto UI thread could not retrieve its next native message.");
            }

            if (result.Value == 0)
            {
                RequestStop();
            }
            else if (message.hwnd.IsNull && message.message == DrainDispatcherMessage)
            {
                try
                {
                    dispatcher.Drain();
                }
                catch (Exception exception)
                {
                    AddFailure(exception, "DrainDispatcher");
                    RequestStop();
                }
            }
            else
            {
                _ = PInvoke.TranslateMessage(message);
                _ = PInvoke.DispatchMessage(message);
            }

            if (Volatile.Read(ref _stopRequested) != 0
                && Volatile.Read(ref _dispatcherShutdownCompleted) != 0
                && dispatcher.PendingCount == 0
                && dispatcher.ActiveOperationCount == 0)
            {
                return;
            }
        }
    }

    private void RequestDispatcherDrain()
    {
        if (!PInvoke.PostThreadMessage(_nativeThreadId, DrainDispatcherMessage, default, default))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Nanto UI dispatcher could not wake its native message loop.");
        }
    }

    private void ShutdownDispatcher(WindowsUiDispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _dispatcherShutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            dispatcher.Dispose();
        }
        catch (Exception exception)
        {
            AddFailure(exception, "DisposeDispatcher");
        }
        finally
        {
            Volatile.Write(ref _dispatcherShutdownCompleted, 1);
            try
            {
                RequestDispatcherDrain();
            }
            catch (Exception exception)
            {
                AddFailure(exception, "WakeDispatcher");
            }
        }
    }

    private void AddFailure(Exception exception, string operation)
    {
        WindowsDiagnostics.UiThreadFailed(_logger, operation, exception.GetType().Name, exception.HResult);
        lock (_failureGate)
        {
            _failures ??= [];
            _failures.Add(exception);
        }
    }

    private void CompleteThread()
    {
        Exception? failure;
        lock (_failureGate)
        {
            failure = _failures switch
            {
                null => null,
                [var exception] => exception,
                _ => new AggregateException("The Nanto UI thread encountered multiple failures.", _failures),
            };
        }

        if (failure is null)
        {
            WindowsDiagnostics.UiThreadStopped(_logger, _timeProvider.GetElapsedTime(_startedAt).TotalMilliseconds);
            _completion.TrySetResult();
        }
        else
        {
            WindowsDiagnostics.UiThreadStopped(_logger, _timeProvider.GetElapsedTime(_startedAt).TotalMilliseconds);
            _completion.TrySetException(failure);
        }
    }
}
