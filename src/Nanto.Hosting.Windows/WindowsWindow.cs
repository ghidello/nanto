using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Nanto.Hosting;

using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows;

internal sealed class WindowsWindow : INantoWindow, IAsyncDisposable
{
    private static readonly Action<ILogger, Exception?> _stateHandlerFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(100, "WindowStateHandlerFailed"),
        "A window state-change handler threw an exception.");
    private static readonly Action<ILogger, Exception?> _rendererHandlerFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(101, "RendererFailureHandlerFailed"),
        "A renderer-failure handler threw an exception.");

    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IUiDispatcher _dispatcher;
    private readonly WindowLifecycle _lifecycle;
    private readonly ILogger _logger;
    private readonly Win32Window _nativeWindow;
    private readonly bool _nativeWindowCreated;
    private readonly TimeProvider _timeProvider;
    private IWindowsWebViewWindow? _webViewWindow;
    private Exception? _closeFailure;
    private EventHandler<RendererFailedEventArgs>? _rendererFailed;
    private WindowsWindowSnapshot _snapshot;
    private int _closeCleanupInProgress;
    private int _closeRequested;

    public WindowId Id { get; }

    public string Title => Volatile.Read(ref _snapshot).Title;

    public WindowSize Size => Volatile.Read(ref _snapshot).Size;

    public WindowState State => Volatile.Read(ref _snapshot).State;

    public bool IsVisible => Volatile.Read(ref _snapshot).IsVisible;

    internal HWND Handle => _nativeWindow.Handle;

    public event EventHandler<WindowStateChangedEventArgs>? StateChanged;

    public event EventHandler<RendererFailedEventArgs>? RendererFailed
    {
        add => _rendererFailed += value;
        remove => _rendererFailed -= value;
    }

    private WindowsWindow(
        Win32WindowClass windowClass,
        ResourceLedger resourceLedger,
        IUiDispatcher dispatcher,
        WindowOptions options,
        IPhase1FailureInjector? failureInjector = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(windowClass);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentOutOfRangeException.ThrowIfEqual(options.InitialSize, default, nameof(options.InitialSize));
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The Windows window must be created on the UI thread.");
        }

        Id = WindowId.Create();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecycle = new WindowLifecycle(_timeProvider);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsWindow>();
        _snapshot = new WindowsWindowSnapshot(options.Title, options.InitialSize, WindowState.Created, false);
        TransitionTo(WindowState.Initializing);

        var style = WINDOW_STYLE.WS_OVERLAPPEDWINDOW;
        if (!options.Resizable)
        {
            style &= ~(WINDOW_STYLE.WS_MAXIMIZEBOX | WINDOW_STYLE.WS_THICKFRAME);
        }

        _nativeWindow = new Win32Window(
            windowClass,
            resourceLedger,
            dispatcher,
            options.Title,
            options.InitialSize.Width,
            options.InitialSize.Height,
            default,
            style,
            new Win32WindowCallbacks
            {
                CloseRequested = CloseOnUiThread,
                Destroyed = OnNativeDestroyed,
                Resized = OnNativeResized,
            },
            failureInjector);
        _nativeWindowCreated = true;
    }

    public static async ValueTask<WindowsWindow> CreateAsync(
        Win32WindowClass windowClass,
        ResourceLedger resourceLedger,
        IUiDispatcher dispatcher,
        IWindowsWebViewApplication webViewApplication,
        WindowOptions options,
        ColorSchemePreference preferredColorScheme,
        IPhase1FailureInjector? failureInjector = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webViewApplication);
        var window = new WindowsWindow(
            windowClass,
            resourceLedger,
            dispatcher,
            options,
            failureInjector,
            timeProvider,
            loggerFactory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            window._webViewWindow = await webViewApplication.CreateWindowAsync(
                window.Handle,
                options,
                preferredColorScheme,
                window.RaiseRendererFailed,
                window.RequestClose,
                window.CanAttemptRendererRecovery,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            window.TransitionTo(WindowState.Running);
            if (options.StartVisible)
            {
                window._nativeWindow.Activate();
                window.PublishSnapshot(title: null, size: null, state: null, isVisible: true);
            }

            return window;
        }
        catch (Exception creationException)
        {
            try
            {
                await window.CloseCoreAsync();
                await window._closeCompletion.Task;
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Windows window creation failed and cleanup also failed.",
                    creationException,
                    cleanupException);
            }

            throw;
        }
    }

    public ValueTask ActivateAsync(CancellationToken cancellationToken = default) => InvokeMutationAsync(
        () =>
        {
            _nativeWindow.Activate();
            PublishSnapshot(title: null, size: null, state: null, isVisible: true);
        },
        cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        RequestClose();
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        return new ValueTask(_closeCompletion.Task.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        RequestClose();
        return new ValueTask(_closeCompletion.Task);
    }

    public ValueTask SetSizeAsync(WindowSize size, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(size, default);
        return InvokeMutationAsync(
            () =>
            {
                _nativeWindow.SetClientSize(
                    DpiConversions.ToPixels(size.Width, _nativeWindow.Dpi, nameof(size)),
                    DpiConversions.ToPixels(size.Height, _nativeWindow.Dpi, nameof(size)));
            },
            cancellationToken);
    }

    public ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return InvokeMutationAsync(
            () =>
            {
                _nativeWindow.SetTitle(title);
                PublishSnapshot(title, size: null, state: null, isVisible: null);
            },
            cancellationToken);
    }

    private void CloseOnUiThread()
    {
        if (State is WindowState.Closing or WindowState.Closed)
        {
            return;
        }

        Interlocked.Exchange(ref _closeRequested, 1);
        TransitionTo(WindowState.Closing);
        _ = ObserveCloseCoreDispatchAsync(
            _dispatcher.InvokeAsync(_ => new ValueTask(CloseCoreAsync()), CancellationToken.None));
    }

    private async Task CloseCoreAsync()
    {
        Volatile.Write(ref _closeCleanupInProgress, 1);
        if (State is not WindowState.Closing and not WindowState.Closed)
        {
            TransitionTo(WindowState.Closing);
        }

        List<Exception>? failures = null;
        var webViewWindow = Interlocked.Exchange(ref _webViewWindow, null);
        if (webViewWindow is not null)
        {
            try
            {
                await webViewWindow.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures = [exception];
            }
        }

        _closeFailure = failures switch
        {
            null => null,
            [var failure] => failure,
            _ => new AggregateException("The Windows window encountered multiple cleanup failures.", failures),
        };

        try
        {
            _nativeWindow.Dispose();
        }
        catch (Exception exception)
        {
            failures ??= [];
            failures.Add(exception);
            _closeFailure = failures switch
            {
                [var failure] => failure,
                _ => new AggregateException("The Windows window encountered multiple cleanup failures.", failures),
            };
        }

        Volatile.Write(ref _closeCleanupInProgress, 0);
        CompleteClose();
    }

    private async Task ObserveCloseCoreDispatchAsync(ValueTask dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _closeCompletion.TrySetException(exception);
        }
    }

    private ValueTask InvokeMutationAsync(Action mutation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(State is WindowState.Closing or WindowState.Closed or WindowState.Failed, this);
        return _dispatcher.InvokeAsync(
            () =>
            {
                ObjectDisposedException.ThrowIf(State is WindowState.Closing or WindowState.Closed or WindowState.Failed, this);
                mutation();
            },
            cancellationToken);
    }

    private void OnNativeDestroyed()
    {
        if (State is not WindowState.Closing)
        {
            TransitionTo(WindowState.Closing);
        }

        TransitionTo(WindowState.Closed);
        if (_nativeWindowCreated && Volatile.Read(ref _closeCleanupInProgress) == 0)
        {
            CompleteClose();
        }
    }

    private void OnNativeResized(int width, int height, uint dpi)
    {
        PublishSnapshot(
            title: null,
            new WindowSize(DpiConversions.ToDips(width, dpi), DpiConversions.ToDips(height, dpi)),
            state: null,
            isVisible: null);
        _webViewWindow?.SetBounds(width, height);
    }

    private void CompleteClose()
    {
        if (!_nativeWindow.IsDestroyed && _closeFailure is null)
        {
            return;
        }

        if (_closeFailure is null)
        {
            _closeCompletion.TrySetResult();
        }
        else
        {
            _closeCompletion.TrySetException(_closeFailure);
        }
    }

    private void PublishSnapshot(string? title, WindowSize? size, WindowState? state, bool? isVisible)
    {
        var current = Volatile.Read(ref _snapshot);
        Volatile.Write(
            ref _snapshot,
            current with
            {
                Title = title ?? current.Title,
                Size = size ?? current.Size,
                State = state ?? current.State,
                IsVisible = isVisible ?? current.IsVisible,
            });
    }

    private void RequestClose()
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
        {
            return;
        }

        _ = RequestCloseAsync();
    }

    private void RaiseRendererFailed(RendererFailureKind kind, string description, bool willAttemptRecovery)
    {
        var eventArgs = new RendererFailedEventArgs
        {
            Kind = kind,
            Description = description,
            WillAttemptRecovery = willAttemptRecovery,
            OccurredAt = _timeProvider.GetUtcNow(),
        };
        foreach (EventHandler<RendererFailedEventArgs> handler in _rendererFailed?.GetInvocationList() ?? [])
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

    private bool CanAttemptRendererRecovery() => State == WindowState.Running && Volatile.Read(ref _closeRequested) == 0;

    private async Task RequestCloseAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(_nativeWindow.RequestClose, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _closeCompletion.TrySetException(exception);
        }
    }

    private void TransitionTo(WindowState state, Exception? failure = null)
    {
        var eventArgs = _lifecycle.TransitionTo(state, failure);
        PublishSnapshot(title: null, size: null, state, isVisible: state == WindowState.Closed ? false : null);
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
}