using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

using Nanto.Hosting.Windows.TestProtocol;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows.TestApp.Interop;

public static unsafe class VisibleDesktopAutomation
{
    private const uint BitmapFileHeaderSize = 14;
    private const uint BitmapInfoHeaderSize = 40;
    private const ushort BitmapSignature = 0x4D42;

    public static VisibleMonitor[] CaptureTopology()
    {
        var previousContext = TestPInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previousContext.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not enter Per-Monitor-V2 awareness.");
        }

        var state = new MonitorEnumerationState();
        var stateHandle = GCHandle.Alloc(state);
        VisibleMonitor[]? monitors = null;
        Exception? enumerationFailure = null;
        try
        {
            if (!TestPInvoke.EnumDisplayMonitors(default, null, &EnumerateMonitor, new LPARAM(GCHandle.ToIntPtr(stateHandle))))
            {
                if (state.Failure is { } failure)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not enumerate monitors.");
            }

            monitors = [.. state.Monitors.OrderByDescending(static monitor => monitor.Observation.IsPrimary)
                .ThenBy(static monitor => monitor.Observation.Left)
                .ThenBy(static monitor => monitor.Observation.Top)
                .ThenBy(static monitor => monitor.Observation.Right)
                .ThenBy(static monitor => monitor.Observation.Bottom)];
        }
        catch (Exception exception)
        {
            enumerationFailure = exception;
        }
        finally
        {
            stateHandle.Free();
        }

        var restoredContext = TestPInvoke.SetThreadDpiAwarenessContext(previousContext);
        if (enumerationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(enumerationFailure).Throw();
        }

        if (restoredContext.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not restore thread DPI awareness.");
        }

        return monitors!;
    }

    public static Phase1WindowObservation CaptureWindow(nint windowHandle, WindowSize windowSize, string stage, string? screenshotPath)
    {
        var window = new HWND(windowHandle);
        if (!TestPInvoke.GetWindowRect(window, out var bounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not read the window bounds.");
        }

        var focusedWindow = TestPInvoke.GetFocus();
        return new Phase1WindowObservation
        {
            Stage = stage,
            Left = bounds.left,
            Top = bounds.top,
            Right = bounds.right,
            Bottom = bounds.bottom,
            ClientWidth = windowSize.Width,
            ClientHeight = windowSize.Height,
            Dpi = TestPInvoke.GetDpiForWindow(window),
            IsForeground = TestPInvoke.GetForegroundWindow() == window,
            HasKeyboardFocus = focusedWindow == window || TestPInvoke.IsChild(window, focusedWindow),
            NativeDarkFrame = ReadDarkFrame(window),
            ScreenshotPath = screenshotPath,
        };
    }

    public static void CaptureScreenshot(nint windowHandle, string destinationPath)
    {
        var window = new HWND(windowHandle);
        if (!TestPInvoke.GetWindowRect(window, out var bounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not read screenshot bounds.");
        }

        var width = bounds.right - bounds.left;
        var height = bounds.bottom - bounds.top;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var screen = TestPInvoke.GetDC(default);
        if (screen.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not acquire the desktop device context.");
        }

        HDC memory = default;
        HBITMAP bitmap = default;
        HGDIOBJ previous = default;
        try
        {
            memory = TestPInvoke.CreateCompatibleDC(screen);
            bitmap = TestPInvoke.CreateCompatibleBitmap(screen, width, height);
            if (memory.IsNull || bitmap.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not create screenshot GDI resources.");
            }

            previous = TestPInvoke.SelectObject(memory, bitmap);
            if (previous.IsNull || (nint)previous.Value == -1)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not select the screenshot bitmap.");
            }

            if (!TestPInvoke.BitBlt(memory, 0, 0, width, height, screen, bounds.left, bounds.top, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not copy the composed desktop region.");
            }

            WriteBitmap(screen, bitmap, width, height, destinationPath);
        }
        finally
        {
            if (!previous.IsNull && (nint)previous.Value != -1)
            {
                _ = TestPInvoke.SelectObject(memory, previous);
            }

            if (!bitmap.IsNull)
            {
                _ = TestPInvoke.DeleteObject(bitmap);
            }

            if (!memory.IsNull)
            {
                _ = TestPInvoke.DeleteDC(memory);
            }

            _ = TestPInvoke.ReleaseDC(default, screen);
        }
    }

    public static uint GetDpi(nint windowHandle) => TestPInvoke.GetDpiForWindow(new HWND(windowHandle));

    public static void EnsureForegroundFocus(nint windowHandle)
    {
        var window = new HWND(windowHandle);
        var focusedWindow = TestPInvoke.GetFocus();
        if (TestPInvoke.GetForegroundWindow() == window && (focusedWindow == window || TestPInvoke.IsChild(window, focusedWindow)))
        {
            return;
        }

        var foregroundWindow = TestPInvoke.GetForegroundWindow();
        var currentThreadId = TestPInvoke.GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow.IsNull ? 0 : TestPInvoke.GetWindowThreadProcessId(foregroundWindow, null);
        var attached = foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && TestPInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, true);
        Exception? detachFailure = null;
        Exception? operationFailure = null;
        try
        {
            if (!TestPInvoke.SetForegroundWindow(window))
            {
                throw new InvalidOperationException("Windows denied foreground promotion for the visible Nanto test window.");
            }

            _ = TestPInvoke.SetFocus(window);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            if (attached && !TestPInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, false))
            {
                detachFailure = new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not detach the foreground input queue.");
            }
        }

        if (operationFailure is not null && detachFailure is not null)
        {
            throw new AggregateException("Foreground promotion and input-queue detachment both failed.", operationFailure, detachFailure);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (detachFailure is not null)
        {
            ExceptionDispatchInfo.Capture(detachFailure).Throw();
        }
    }

    public static void MoveWindow(nint windowHandle, Phase1MonitorObservation monitor)
    {
        var window = new HWND(windowHandle);
        if (!TestPInvoke.GetWindowRect(window, out var bounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not read the window before moving it.");
        }

        var width = bounds.right - bounds.left;
        var height = bounds.bottom - bounds.top;
        var workWidth = monitor.WorkRight - monitor.WorkLeft;
        var workHeight = monitor.WorkBottom - monitor.WorkTop;
        if (width > workWidth || height > workHeight)
        {
            throw new InvalidOperationException("The visible test window does not fit completely inside the selected monitor work area.");
        }

        var left = monitor.WorkLeft + (workWidth - width) / 2;
        var top = monitor.WorkTop + (workHeight - height) / 2;
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        if (!TestPInvoke.SetWindowPos(window, default, left, top, 0, 0, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not move the window.");
        }
    }

    public static void SendF6(nint windowHandle)
    {
        var window = new HWND(windowHandle);
        var focusedWindow = TestPInvoke.GetFocus();
        if (TestPInvoke.GetForegroundWindow() != window || focusedWindow != window && !TestPInvoke.IsChild(window, focusedWindow))
        {
            throw new InvalidOperationException("The visible test window lost foreground keyboard focus before F6 input.");
        }

        INPUT* inputs = stackalloc INPUT[2];
        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki.wVk = VIRTUAL_KEY.VK_F6;
        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki.wVk = VIRTUAL_KEY.VK_F6;
        inputs[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        var sent = TestPInvoke.SendInput(new ReadOnlySpan<INPUT>(inputs, 2), sizeof(INPUT));
        if (sent != 2)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not send F6.");
        }
    }

    private static bool ReadDarkFrame(HWND window)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        var result = TestPInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, value);
        if (result.Failed)
        {
            Marshal.ThrowExceptionForHR(result.Value);
        }

        return BitConverter.ToInt32(value) != 0;
    }

    private static void WriteBitmap(HDC deviceContext, HBITMAP bitmap, int width, int height, string destinationPath)
    {
        var rowLength = checked((width * 3 + 3) & ~3);
        var imageLength = checked(rowLength * height);
        var pixels = new byte[imageLength];
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = BitmapInfoHeaderSize,
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 24,
                biCompression = (uint)BI_COMPRESSION.BI_RGB,
                biSizeImage = (uint)imageLength,
            },
        };
        fixed (byte* pixelsPointer = pixels)
        {
            if (TestPInvoke.GetDIBits(deviceContext, bitmap, 0, (uint)height, pixelsPointer, &info, DIB_USAGE.DIB_RGB_COLORS) != height)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not read screenshot pixels.");
            }
        }

        using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write(BitmapSignature);
        writer.Write(checked(BitmapFileHeaderSize + BitmapInfoHeaderSize + (uint)imageLength));
        writer.Write(0u);
        writer.Write(BitmapFileHeaderSize + BitmapInfoHeaderSize);
        writer.Write(BitmapInfoHeaderSize);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0u);
        writer.Write((uint)imageLength);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(pixels);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumerateMonitor(HMONITOR monitor, HDC deviceContext, RECT* bounds, LPARAM statePointer)
    {
        var state = (MonitorEnumerationState?)GCHandle.FromIntPtr(statePointer.Value).Target;
        if (state is null)
        {
            return false;
        }

        try
        {
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!TestPInvoke.GetMonitorInfo(monitor, ref info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The visible test could not read monitor information.");
            }

            var result = TestPInvoke.GetScaleFactorForMonitor(monitor, out var scaleFactor);
            if (result.Failed)
            {
                Marshal.ThrowExceptionForHR(result.Value);
            }

            if (scaleFactor == DEVICE_SCALE_FACTOR.DEVICE_SCALE_FACTOR_INVALID)
            {
                throw new InvalidOperationException("The visible test received an invalid monitor scale factor.");
            }

            var effectiveDpi = checked((uint)Math.Round((int)scaleFactor * 96d / 100d, MidpointRounding.AwayFromZero));

            state.Monitors.Add(new VisibleMonitor(new Phase1MonitorObservation
            {
                Left = info.rcMonitor.left,
                Top = info.rcMonitor.top,
                Right = info.rcMonitor.right,
                Bottom = info.rcMonitor.bottom,
                WorkLeft = info.rcWork.left,
                WorkTop = info.rcWork.top,
                WorkRight = info.rcWork.right,
                WorkBottom = info.rcWork.bottom,
                IsPrimary = (info.dwFlags & TestPInvoke.MONITORINFOF_PRIMARY) != 0,
                EffectiveDpi = effectiveDpi,
            }));
            return true;
        }
        catch (Exception exception)
        {
            state.Failure = exception;
            return false;
        }
    }

    public sealed record VisibleMonitor(Phase1MonitorObservation Observation);

    private sealed class MonitorEnumerationState
    {
        public List<VisibleMonitor> Monitors { get; } = [];

        public Exception? Failure { get; set; }
    }
}
