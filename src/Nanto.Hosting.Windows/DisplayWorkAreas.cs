using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Nanto.Hosting.Windows;

internal static unsafe class DisplayWorkAreas
{
    public static RECT[] GetCurrent()
    {
        var state = new EnumerationState();
        var stateHandle = GCHandle.Alloc(state);
        try
        {
            if (!PInvoke.EnumDisplayMonitors(default, null, &EnumerateMonitor, new LPARAM(GCHandle.ToIntPtr(stateHandle))))
            {
                if (state.Failure is { } failure)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not enumerate the current display monitors.");
            }

            return [.. state.WorkAreas];
        }
        finally
        {
            stateHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumerateMonitor(HMONITOR monitor, HDC deviceContext, RECT* monitorBounds, LPARAM statePointer)
    {
        var state = (EnumerationState?)GCHandle.FromIntPtr(statePointer.Value).Target;
        if (state is null)
        {
            return false;
        }

        try
        {
            var monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo))
            {
                state.Failure = new Win32Exception(Marshal.GetLastPInvokeError(), "Nanto could not read a display monitor work area.");
                return false;
            }

            state.WorkAreas.Add(monitorInfo.rcWork);
            return true;
        }
        catch (Exception exception)
        {
            state.Failure = exception;
            return false;
        }
    }

    private sealed class EnumerationState
    {
        public List<RECT> WorkAreas { get; } = [];

        public Exception? Failure { get; set; }
    }
}