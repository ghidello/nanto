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

    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IUiDispatcher _dispatcher;
    private readonly WindowLifecycle _lifecycle;
    private readonly ILogger _logger;
    private readonly Win32Window _nativeWindow;
    private IWindowsWebViewWindow? _webViewWindow;
    private Exception? _closeFailure;
    private EventHandler<RendererFailedEventArgs>? _rendererFailed;
    private WindowsWindowSnapshot _snapshot;
    private int _closeCleanupInProgress;
    private int _closeRequested;
    private int _nativeWindowCreated;

    public WindowId Id { get; }

    public string Title => Volatile.Read(ref _snapshot).Title;

    public WindowBounds Bounds => Volatile.Read(ref _snapshot).Bounds;

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
        ArgumentOutOfRangeException.ThrowIfEqual(options.InitialBounds, default, nameof(options));
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The Windows window must be created on the UI thread.");
        }

        Id = WindowId.Create();
        _lifecycle = new WindowLifecycle(timeProvider ?? TimeProvider.System);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsWindow>();
        _snapshot = new WindowsWindowSnapshot(options.Title, options.InitialBounds, WindowState.Created, false);
        TransitionTo(WindowState.Initializing);

        var bounds = ConvertBounds(options.InitialBounds);
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
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            default,
            style,
            new Win32WindowCallbacks
            {
                CloseRequested = CloseOnUiThread,
                Destroyed = OnNativeDestroyed,
                Resized = OnNativeResized,
            },
            failureInjector);
        Volatile.Write(ref _nativeWindowCreated, 1);
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
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            window.TransitionTo(WindowState.Running);
            if (options.StartVisible)
            {
                window._nativeWindow.Activate();
                window.PublishSnapshot(title: null, bounds: null, state: null, isVisible: true);
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
            PublishSnapshot(title: null, bounds: null, state: null, isVisible: true);
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

    public ValueTask SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(bounds, default);
        var nativeBounds = ConvertBounds(bounds);
        return InvokeMutationAsync(
            () =>
            {
                _nativeWindow.SetBounds(nativeBounds.X, nativeBounds.Y, nativeBounds.Width, nativeBounds.Height);
                PublishSnapshot(title: null, bounds, state: null, isVisible: null);
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
                PublishSnapshot(title, bounds: null, state: null, isVisible: null);
            },
            cancellationToken);
    }

    private static NativeBounds ConvertBounds(WindowBounds bounds)
    {
        try
        {
            return new NativeBounds(
                checked((int)Math.Round(bounds.X, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(bounds.Y, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(bounds.Width, MidpointRounding.AwayFromZero)),
                checked((int)Math.Round(bounds.Height, MidpointRounding.AwayFromZero)));
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), bounds, "Window bounds cannot be represented by Win32 coordinates.");
        }
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
        if (Volatile.Read(ref _nativeWindowCreated) != 0 && Volatile.Read(ref _closeCleanupInProgress) == 0)
        {
            CompleteClose();
        }
    }

    private void OnNativeResized(int width, int height)
    {
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

    private void PublishSnapshot(string? title, WindowBounds? bounds, WindowState? state, bool? isVisible)
    {
        var current = Volatile.Read(ref _snapshot);
        Volatile.Write(
            ref _snapshot,
            current with
            {
                Title = title ?? current.Title,
                Bounds = bounds ?? current.Bounds,
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
        PublishSnapshot(title: null, bounds: null, state, isVisible: state == WindowState.Closed ? false : null);
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

    private sealed record NativeBounds(int X, int Y, int Width, int Height);
}
