using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows;

internal sealed unsafe class Win32Window : IDisposable
{
    private static readonly ConcurrentDictionary<HWND, Win32Window> Windows = new();

    private readonly Win32WindowCallbacks? _callbacks;
    private readonly Lock _callbackFailureGate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly WINDOW_EX_STYLE _extendedStyle;
    private readonly WINDOW_STYLE _style;
    private List<Exception>? _callbackFailures;
    private int _closeRequested;
    private uint _dpi;
    private HWND _handle;
    private IDisposable? _nativeHandleLease;
    private bool _restoreToMaximized;
    private IDisposable? _windowLease;

    public HWND Handle => _handle;

    public uint Dpi => Volatile.Read(ref _dpi);

    public bool IsDestroyed => _handle.IsNull;

    internal static delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT> WindowProcedure => &ProcessWindowMessage;

    public Win32Window(
        Win32WindowClass windowClass,
        ResourceLedger resourceLedger,
        IUiDispatcher dispatcher,
        string title,
        double initialClientWidth,
        double initialClientHeight,
        WINDOW_EX_STYLE extendedStyle,
        WINDOW_STYLE style,
        Win32WindowCallbacks? callbacks = null,
        IPhase1FailureInjector? failureInjector = null,
        Func<HWND, bool>? destroyWindow = null)
    {
        ArgumentNullException.ThrowIfNull(windowClass);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        _callbacks = callbacks;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialClientWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialClientHeight);
        ThrowIfNotOnUiThread();

        _extendedStyle = extendedStyle;
        _style = style;
        failureInjector ??= NoOpPhase1FailureInjector.Instance;
        destroyWindow ??= static handle => PInvoke.DestroyWindow(handle);
        _windowLease = resourceLedger.Acquire(WindowsResourceKind.Window, "Window");
        var isRegistered = false;
        try
        {
            var initialWindowSize = GetWindowSizeForClientArea(
                DpiConversions.ToPixels(initialClientWidth, DpiConversions.DefaultDpi, nameof(initialClientWidth)),
                DpiConversions.ToPixels(initialClientHeight, DpiConversions.DefaultDpi, nameof(initialClientHeight)),
                style,
                extendedStyle,
                DpiConversions.DefaultDpi);
            _handle = PInvoke.CreateWindowEx(
                extendedStyle,
                windowClass.Name,
                title,
                style,
                PInvoke.CW_USEDEFAULT,
                PInvoke.CW_USEDEFAULT,
                initialWindowSize.Width,
                initialWindowSize.Height,
                default,
                null,
                windowClass.ModuleHandle,
                null);
            if (_handle.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not create its native window.");
            }

            if (!Windows.TryAdd(_handle, this))
            {
                throw new InvalidOperationException("The native window handle is already registered.");
            }

            isRegistered = true;
            _nativeHandleLease = resourceLedger.Acquire(WindowsResourceKind.NativeHandle, "WindowHandle");
            _dpi = PInvoke.GetDpiForWindow(_handle);
            if (_dpi == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not determine the native window DPI.");
            }

            SetClientSize(
                DpiConversions.ToPixels(initialClientWidth, _dpi, nameof(initialClientWidth)),
                DpiConversions.ToPixels(initialClientHeight, _dpi, nameof(initialClientHeight)));
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WindowCreated);
        }
        catch (Exception creationException)
        {
            if (!_handle.IsNull && !destroyWindow(_handle))
            {
                var cleanupException = new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not roll back native window creation.");
                throw new AggregateException("Nanto native window creation failed and its rollback also failed.", creationException, cleanupException);
            }

            if (isRegistered && !_handle.IsNull)
            {
                throw new AggregateException(
                    "Nanto native window creation failed and its rollback did not confirm native destruction.",
                    creationException,
                    new InvalidOperationException("DestroyWindow completed without delivering WM_NCDESTROY to the native window owner."));
            }

            _handle = default;
            Interlocked.Exchange(ref _nativeHandleLease, null)?.Dispose();
            Interlocked.Exchange(ref _windowLease, null)?.Dispose();

            var callbackFailure = TakeCallbackFailure();
            if (callbackFailure is not null)
            {
                throw new AggregateException(
                    "Nanto native window creation failed and its rollback callback also failed.",
                    creationException,
                    callbackFailure);
            }

            throw;
        }
    }

    public void Dispose()
    {
        ThrowIfNotOnUiThread();
        var handle = _handle;
        if (!handle.IsNull && !PInvoke.DestroyWindow(handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not destroy its native window.");
        }

        if (!_handle.IsNull)
        {
            throw new InvalidOperationException("DestroyWindow completed without delivering WM_NCDESTROY to the native window owner.");
        }

        var callbackFailure = TakeCallbackFailure();
        if (callbackFailure is not null)
        {
            ExceptionDispatchInfo.Capture(callbackFailure).Throw();
        }
    }

    public void RequestClose()
    {
        ThrowIfNotOnUiThread();
        ThrowIfDestroyed();

        if (!PInvoke.PostMessage(_handle, PInvoke.WM_CLOSE, default, default))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not request native window closure.");
        }
    }

    public void Activate()
    {
        ThrowIfNotOnUiThread();
        ThrowIfDestroyed();

        _ = PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOW);
        _ = PInvoke.SetForegroundWindow(_handle);
        _ = PInvoke.SetFocus(_handle);
    }

    public void SetClientSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ThrowIfNotOnUiThread();
        ThrowIfDestroyed();

        var windowSize = GetWindowSizeForClientArea(width, height, _style, _extendedStyle, _dpi);
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        if (!PInvoke.SetWindowPos(_handle, default, 0, 0, windowSize.Width, windowSize.Height, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not update its native window client size.");
        }
    }

    public void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        ThrowIfNotOnUiThread();
        ThrowIfDestroyed();

        if (!PInvoke.SetWindowText(_handle, title))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not update its native window title.");
        }
    }

    private void CorrectPlacementIfInaccessible()
    {
        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!PInvoke.GetWindowPlacement(_handle, ref placement))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not read its native window placement state.");
        }

        if (placement.showCmd != SHOW_WINDOW_CMD.SW_SHOWNORMAL)
        {
            if (placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMINIMIZED && _restoreToMaximized)
            {
                placement.flags |= WINDOWPLACEMENT_FLAGS.WPF_RESTORETOMAXIMIZED;
            }

            if (!PInvoke.SetWindowPlacement(_handle, placement))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not restore its native window placement state to an accessible work area.");
            }

            return;
        }

        if (!PInvoke.GetWindowRect(_handle, out var windowBounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not read its native window placement.");
        }

        var workAreas = DisplayWorkAreas.GetCurrent();
        if (!WindowPlacement.TryGetCorrection(windowBounds, workAreas, out var position))
        {
            return;
        }

        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        if (!PInvoke.SetWindowPos(_handle, default, position.X, position.Y, 0, 0, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not restore its native window to an accessible work area.");
        }
    }

    private void OnNativeDestroyed()
    {
        _handle = default;
        Interlocked.Exchange(ref _nativeHandleLease, null)?.Dispose();
        Interlocked.Exchange(ref _windowLease, null)?.Dispose();
        _callbacks?.Destroyed?.Invoke();
    }

    private bool OnNativeCloseRequested()
    {
        var closeRequested = _callbacks?.CloseRequested;
        if (closeRequested is null)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _closeRequested, 1) == 0)
        {
            closeRequested();
        }

        return true;
    }

    private static NativeSize GetWindowSizeForClientArea(
        int clientWidth,
        int clientHeight,
        WINDOW_STYLE style,
        WINDOW_EX_STYLE extendedStyle,
        uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientHeight);
        var bounds = new RECT(0, 0, clientWidth, clientHeight);
        if (!PInvoke.AdjustWindowRectExForDpi(ref bounds, style, false, extendedStyle, dpi))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not calculate the native frame size.");
        }

        return new NativeSize(
            checked(bounds.right - bounds.left),
            checked(bounds.bottom - bounds.top));
    }

    private void NotifyClientSize()
    {
        if (!PInvoke.GetClientRect(_handle, out var clientBounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not read the native window client size.");
        }

        var width = checked(clientBounds.right - clientBounds.left);
        var height = checked(clientBounds.bottom - clientBounds.top);
        if (width > 0 && height > 0)
        {
            _callbacks?.Resized?.Invoke(width, height, _dpi);
        }
    }

    private void OnNativeResized(WPARAM wParam)
    {
        _restoreToMaximized = GetRestoreToMaximized(_restoreToMaximized, (uint)wParam.Value);
        NotifyClientSize();
    }

    internal static bool GetRestoreToMaximized(bool currentValue, uint sizeState) => sizeState switch
    {
        PInvoke.SIZE_MAXIMIZED => true,
        PInvoke.SIZE_RESTORED => false,
        _ => currentValue,
    };

    private void OnDpiChanged(WPARAM wParam, LPARAM lParam)
    {
        var packedDpi = (nuint)wParam.Value;
        var horizontalDpi = unchecked((ushort)packedDpi);
        var verticalDpi = unchecked((ushort)(packedDpi >> 16));
        if (horizontalDpi == 0 || horizontalDpi != verticalDpi || lParam.Value == 0)
        {
            throw new InvalidOperationException("Windows delivered an invalid DPI-change message.");
        }

        var previousDpi = _dpi;
        _dpi = horizontalDpi;
        try
        {
            var suggestedBounds = *(RECT*)lParam.Value;
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
            if (!PInvoke.SetWindowPos(
                    _handle,
                    default,
                    suggestedBounds.left,
                    suggestedBounds.top,
                    checked(suggestedBounds.right - suggestedBounds.left),
                    checked(suggestedBounds.bottom - suggestedBounds.top),
                    flags))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not apply the DPI-adjusted window bounds.");
            }
        }
        catch
        {
            _dpi = previousDpi;
            throw;
        }

        NotifyClientSize();
    }

    private void RecordCallbackFailure(Exception exception)
    {
        lock (_callbackFailureGate)
        {
            _callbackFailures ??= [];
            _callbackFailures.Add(exception);
        }
    }

    private Exception? TakeCallbackFailure()
    {
        lock (_callbackFailureGate)
        {
            var callbackFailure = _callbackFailures switch
            {
                null => null,
                [var exception] => exception,
                _ => new AggregateException("Native window callbacks encountered multiple failures.", _callbackFailures),
            };
            _callbackFailures = null;
            return callbackFailure;
        }
    }

    private void ThrowIfDestroyed()
    {
        ObjectDisposedException.ThrowIf(_handle.IsNull, this);
    }

    private void ThrowIfNotOnUiThread()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The native window is owned by the UI thread.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT ProcessWindowMessage(HWND windowHandle, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_SIZE && Windows.TryGetValue(windowHandle, out var resizedWindow))
        {
            try
            {
                resizedWindow.OnNativeResized(wParam);
            }
            catch (Exception exception)
            {
                resizedWindow.RecordCallbackFailure(exception);
            }
        }

        if (message == PInvoke.WM_DPICHANGED && Windows.TryGetValue(windowHandle, out var dpiChangedWindow))
        {
            try
            {
                dpiChangedWindow.OnDpiChanged(wParam, lParam);
                return default;
            }
            catch (Exception exception)
            {
                dpiChangedWindow.RecordCallbackFailure(exception);
            }
        }

        if ((message == PInvoke.WM_DISPLAYCHANGE
                || message == PInvoke.WM_SETTINGCHANGE
                && wParam.Value == (nuint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWORKAREA)
            && Windows.TryGetValue(windowHandle, out var displayChangedWindow))
        {
            try
            {
                displayChangedWindow.CorrectPlacementIfInaccessible();
            }
            catch (Exception exception)
            {
                displayChangedWindow.RecordCallbackFailure(exception);
            }
        }

        if (message == PInvoke.WM_CLOSE && Windows.TryGetValue(windowHandle, out var closingWindow))
        {
            try
            {
                if (closingWindow.OnNativeCloseRequested())
                {
                    return default;
                }
            }
            catch (Exception exception)
            {
                closingWindow.RecordCallbackFailure(exception);
            }
        }

        var result = PInvoke.DefWindowProc(windowHandle, message, wParam, lParam);
        if (message == PInvoke.WM_NCDESTROY && Windows.TryRemove(windowHandle, out var window))
        {
            try
            {
                window.OnNativeDestroyed();
            }
            catch (Exception exception)
            {
                window.RecordCallbackFailure(exception);
            }
        }

        return result;
    }

    private readonly record struct NativeSize(int Width, int Height);
}