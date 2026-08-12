using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows;

internal sealed unsafe class Win32WindowClass : IDisposable
{
    private static long _nextClassId;

    private readonly IUiDispatcher _dispatcher;
    private readonly FreeLibrarySafeHandle _moduleHandle;
    private IDisposable? _resourceLease;

    public string Name { get; }

    internal FreeLibrarySafeHandle ModuleHandle => _moduleHandle;

    public Win32WindowClass(
        ResourceLedger resourceLedger,
        IUiDispatcher dispatcher,
        IPhase1FailureInjector? failureInjector = null)
    {
        ArgumentNullException.ThrowIfNull(resourceLedger);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ThrowIfNotOnUiThread();

        failureInjector ??= NoOpPhase1FailureInjector.Instance;
        Name = $"Nanto.Window.{Environment.ProcessId}.{Interlocked.Increment(ref _nextClassId)}";
        _moduleHandle = PInvoke.GetModuleHandle((string?)null);
        if (_moduleHandle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            _moduleHandle.Dispose();
            throw new Win32Exception(error, "Nanto could not obtain its process module handle.");
        }

        var isRegistered = false;
        try
        {
            fixed (char* className = Name)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = Win32Window.WindowProcedure,
                    hInstance = new HINSTANCE(_moduleHandle.DangerousGetHandle()),
                    lpszClassName = className,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Nanto could not register window class '{Name}'.");
                }

                isRegistered = true;
            }

            _resourceLease = resourceLedger.Acquire(WindowsResourceKind.NativeHandle, "WindowClassRegistration");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WindowClassRegistered);
        }
        catch (Exception registrationException)
        {
            if (isRegistered && !PInvoke.UnregisterClass(Name, _moduleHandle))
            {
                var cleanupException = new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Nanto could not roll back window class registration for '{Name}'.");
                throw new AggregateException("Nanto window class registration failed and its rollback also failed.", registrationException, cleanupException);
            }

            Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
            _moduleHandle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        ThrowIfNotOnUiThread();
        if (_resourceLease is null)
        {
            return;
        }

        if (!PInvoke.UnregisterClass(Name, _moduleHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Nanto could not unregister window class '{Name}'.");
        }

        Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
        _moduleHandle.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfNotOnUiThread()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The Win32 window class is owned by the UI thread.");
        }
    }
}
