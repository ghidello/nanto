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

    private readonly IUiDispatcher _dispatcher;
    private Exception? _callbackFailure;
    private HWND _handle;
    private IDisposable? _nativeHandleLease;
    private IDisposable? _windowLease;

    public HWND Handle => _handle;

    public bool IsDestroyed => _handle.IsNull;

    internal static delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT> WindowProcedure => &ProcessWindowMessage;

    public Win32Window(
        Win32WindowClass windowClass,
        ResourceLedger resourceLedger,
        IUiDispatcher dispatcher,
        string title,
        int x,
        int y,
        int width,
        int height,
        WINDOW_EX_STYLE extendedStyle,
        WINDOW_STYLE style,
        IPhase1FailureInjector? failureInjector = null,
        Func<HWND, bool>? destroyWindow = null)
    {
        ArgumentNullException.ThrowIfNull(windowClass);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ThrowIfNotOnUiThread();

        failureInjector ??= NoOpPhase1FailureInjector.Instance;
        destroyWindow ??= static handle => PInvoke.DestroyWindow(handle);
        _windowLease = resourceLedger.Acquire(WindowsResourceKind.Window);
        var isRegistered = false;
        try
        {
            _handle = PInvoke.CreateWindowEx(
                extendedStyle,
                windowClass.Name,
                title,
                style,
                x,
                y,
                width,
                height,
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
            _nativeHandleLease = resourceLedger.Acquire(WindowsResourceKind.NativeHandle);
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

        var callbackFailure = Interlocked.Exchange(ref _callbackFailure, null);
        if (callbackFailure is not null)
        {
            ExceptionDispatchInfo.Capture(callbackFailure).Throw();
        }
    }

    private void OnNativeDestroyed()
    {
        _handle = default;
        Interlocked.Exchange(ref _nativeHandleLease, null)?.Dispose();
        Interlocked.Exchange(ref _windowLease, null)?.Dispose();
    }

    private void RecordCallbackFailure(Exception exception)
    {
        _ = Interlocked.CompareExchange(ref _callbackFailure, exception, null);
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
}
