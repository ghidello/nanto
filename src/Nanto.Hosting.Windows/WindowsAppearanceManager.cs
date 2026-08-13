using System.Runtime.ExceptionServices;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Nanto.Hosting.Windows;

internal sealed class WindowsAppearanceManager : IDisposable
{
    private readonly Action<HWND, bool, NantoFailureStage> _applyFrameAppearance;
    private readonly Lock _callbackFailureGate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowsSystemAppearanceSource _systemAppearance;
    private Exception? _callbackFailure;
    private int _disposed;
    private HWND _window;

    public ColorSchemePreference PreferredColorScheme { get; private set; }

    public WindowsAppearanceManager(
        IUiDispatcher dispatcher,
        IWindowsSystemAppearanceSource systemAppearance,
        Action<HWND, bool, NantoFailureStage>? applyFrameAppearance = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _systemAppearance = systemAppearance ?? throw new ArgumentNullException(nameof(systemAppearance));
        _applyFrameAppearance = applyFrameAppearance ?? ApplyFrameAppearance;
        ThrowIfNotOnUiThread();
        _systemAppearance.Changed += OnSystemAppearanceChanged;
    }

    public static WindowsAppearanceManager Create(
        IUiDispatcher dispatcher,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken)
    {
        var systemAppearance = new WindowsSystemAppearanceSource(resourceLedger, failureInjector, cancellationToken);
        try
        {
            return new WindowsAppearanceManager(dispatcher, systemAppearance);
        }
        catch (Exception creationException)
        {
            try
            {
                systemAppearance.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Windows appearance creation failed and cleanup also failed.",
                    creationException,
                    cleanupException);
            }

            throw;
        }
    }

    public IDisposable AttachWindow(HWND window, ColorSchemePreference preferredColorScheme)
    {
        ThrowIfNotOnUiThread();
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfEqual(window, default, nameof(window));
        if (!_window.IsNull)
        {
            throw new InvalidOperationException("The Phase 1 appearance manager can attach only one window.");
        }

        Apply(window, preferredColorScheme, NantoFailureStage.Startup);
        _window = window;
        PreferredColorScheme = preferredColorScheme;
        return new WindowAttachment(this, window);
    }

    public void Dispose()
    {
        ThrowIfNotOnUiThread();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window = default;
        List<Exception>? cleanupExceptions = null;
        try
        {
            _systemAppearance.Changed -= OnSystemAppearanceChanged;
        }
        catch (Exception exception)
        {
            cleanupExceptions = [exception];
        }

        try
        {
            _systemAppearance.Dispose();
        }
        catch (Exception exception)
        {
            cleanupExceptions ??= [];
            cleanupExceptions.Add(exception);
        }

        lock (_callbackFailureGate)
        {
            if (_callbackFailure is not null)
            {
                cleanupExceptions ??= [];
                cleanupExceptions.Add(_callbackFailure);
                _callbackFailure = null;
            }
        }

        if (cleanupExceptions is [var cleanupException])
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }

        if (cleanupExceptions is { Count: > 1 })
        {
            throw new AggregateException("Windows appearance cleanup encountered multiple failures.", cleanupExceptions);
        }
    }

    public void SetPreferredColorScheme(ColorSchemePreference preferredColorScheme)
    {
        ThrowIfNotOnUiThread();
        ThrowIfDisposed();
        if (PreferredColorScheme == preferredColorScheme)
        {
            return;
        }

        if (!_window.IsNull)
        {
            Apply(_window, preferredColorScheme, NantoFailureStage.Runtime);
        }

        PreferredColorScheme = preferredColorScheme;
    }

    internal static bool ResolveDarkMode(ColorSchemePreference preferredColorScheme, bool systemIsDark) => preferredColorScheme switch
    {
        ColorSchemePreference.System => systemIsDark,
        ColorSchemePreference.Light => false,
        ColorSchemePreference.Dark => true,
        _ => throw new ArgumentOutOfRangeException(
            nameof(preferredColorScheme),
            preferredColorScheme,
            "The preferred color scheme is not supported."),
    };

    private static unsafe void ApplyFrameAppearance(HWND window, bool useDarkMode, NantoFailureStage failureStage)
    {
        var value = useDarkMode ? 1 : 0;
        HResult.ThrowIfFailed(
            PInvoke.DwmSetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &value, sizeof(int)),
            "dwm.window.set-dark-mode",
            failureStage);
    }

    private void Apply(HWND window, ColorSchemePreference preferredColorScheme, NantoFailureStage failureStage)
    {
        var useDarkMode = preferredColorScheme == ColorSchemePreference.System
            ? _systemAppearance.IsDark
            : ResolveDarkMode(preferredColorScheme, systemIsDark: false);
        _applyFrameAppearance(window, useDarkMode, failureStage);
    }

    private void DetachWindow(HWND window)
    {
        ThrowIfNotOnUiThread();
        if (_window == window)
        {
            _window = default;
        }
    }

    private void OnSystemAppearanceChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var dispatch = _dispatcher.InvokeAsync(ApplySystemAppearanceIfSelected, CancellationToken.None);
            if (!dispatch.IsCompletedSuccessfully)
            {
                _ = ObserveSystemAppearanceDispatchAsync(dispatch);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure(exception);
        }
    }

    private void ApplySystemAppearanceIfSelected()
    {
        if (Volatile.Read(ref _disposed) == 0 && PreferredColorScheme == ColorSchemePreference.System && !_window.IsNull)
        {
            Apply(_window, PreferredColorScheme, NantoFailureStage.Runtime);
        }
    }

    private async Task ObserveSystemAppearanceDispatchAsync(ValueTask dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                RecordCallbackFailure(exception);
            }
        }
    }

    private void RecordCallbackFailure(Exception exception)
    {
        lock (_callbackFailureGate)
        {
            _callbackFailure = _callbackFailure is null
                ? exception
                : new AggregateException("System appearance callbacks encountered multiple failures.", _callbackFailure, exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfNotOnUiThread()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Windows appearance is owned by the UI thread.");
        }
    }

    private sealed class WindowAttachment(WindowsAppearanceManager owner, HWND window) : IDisposable
    {
        private WindowsAppearanceManager? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.DetachWindow(window);
    }
}