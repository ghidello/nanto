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
    private EventHandler<RendererFailedEventArgs>? _rendererFailed;
    private WindowsWindowSnapshot _snapshot;
    private int _closeRequested;

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

    public WindowsWindow(
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
            },
            failureInjector);

        TransitionTo(WindowState.Running);
        if (options.StartVisible)
        {
            _nativeWindow.Activate();
            PublishSnapshot(title: null, bounds: null, state: null, isVisible: true);
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

        TransitionTo(WindowState.Closing);
        try
        {
            _nativeWindow.Dispose();
        }
        catch (Exception exception)
        {
            _closeCompletion.TrySetException(exception);
            throw;
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
        _closeCompletion.TrySetResult();
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
