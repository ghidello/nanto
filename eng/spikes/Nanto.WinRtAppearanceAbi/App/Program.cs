using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.WinRtAppearanceAbiSpike;

internal static unsafe class Program
{
    private const string Html = "<!doctype html><title>Nanto ABI spike</title>\n";
    private const int AppearanceCycles = 100;

    private static readonly EnvironmentCreatedHandler _environmentCreatedHandler = new();
    private static readonly ControllerCreatedHandler _controllerCreatedHandler = new();
    private static readonly NavigationCompletedHandler _navigationCompletedHandler = new();

    private static ICoreWebView2Environment? _environment;
    private static ICoreWebView2Controller? _controller;
    private static ICoreWebView2? _webView;
    private static EventRegistrationToken _navigationToken;
    private static HWND _window;
    private static int _exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            return 2;
        }

        string userDataDirectory = Path.GetFullPath(args[0]);
        string reportPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(userDataDirectory);
        ThrowIfFailed(PInvoke.RoInitialize(Windows.Win32.System.WinRT.RO_INIT_TYPE.RO_INIT_SINGLETHREADED));
        try
        {
            bool isDark = RunAppearanceSelfTest();
#if NARROW_ABI
            RunNarrowFailureCleanupSelfTest();
#endif
            RunHiddenWebView(userDataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllLines(
                reportPath,
                [
                    $"mode={GetMode()}",
                    $"isDark={isDark}",
                    $"appearanceCycles={AppearanceCycles}",
                    "syntheticCallbacks=passed",
                    "postDisposalSuppression=passed",
                    "repeatedDisposal=passed",
                    "failureCleanup=passed",
                    "webView=passed",
                ]);
            return _exitCode;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, exception.ToString());
            return 1;
        }
        finally
        {
            CleanupWebView();
            PInvoke.RoUninitialize();
        }
    }

    internal static int EnvironmentCreated(int result, nint environmentPointer)
    {
        try
        {
            ThrowIfFailed(result);
            _environment = Adopt<ICoreWebView2Environment>(environmentPointer);
            void* handler = ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>.ConvertToUnmanaged(
                _controllerCreatedHandler);
            try
            {
                ThrowIfFailed(_environment.CreateCoreWebView2Controller((nint)_window.Value, (nint)handler));
            }
            finally
            {
                ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>.Free(handler);
            }
        }
        catch
        {
            PInvoke.PostQuitMessage(1);
        }

        return 0;
    }

    internal static int ControllerCreated(int result, nint controllerPointer)
    {
        try
        {
            ThrowIfFailed(result);
            _controller = Adopt<ICoreWebView2Controller>(controllerPointer);
            nint webViewPointer = 0;
            ThrowIfFailed(_controller.get_CoreWebView2((nint)(&webViewPointer)));
            _webView = Adopt<ICoreWebView2>(webViewPointer);
            ThrowIfFailed(_controller.put_Bounds(new WebView2Rect(0, 0, 320, 240)));

            void* handler = ComInterfaceMarshaller<ICoreWebView2NavigationCompletedEventHandler>.ConvertToUnmanaged(
                _navigationCompletedHandler);
            try
            {
                EventRegistrationToken token = default;
                ThrowIfFailed(_webView.add_NavigationCompleted((nint)handler, (nint)(&token)));
                _navigationToken = token;
            }
            finally
            {
                ComInterfaceMarshaller<ICoreWebView2NavigationCompletedEventHandler>.Free(handler);
            }

            nint html = Marshal.StringToCoTaskMemUni(Html);
            try
            {
                ThrowIfFailed(_webView.NavigateToString(html));
            }
            finally
            {
                Marshal.FreeCoTaskMem(html);
            }
        }
        catch
        {
            PInvoke.PostQuitMessage(1);
        }

        return 0;
    }

    internal static int NavigationCompleted()
    {
        _exitCode = 0;
        PInvoke.PostQuitMessage(0);
        return 0;
    }

    private static bool RunAppearanceSelfTest()
    {
        bool? observedDark = null;
        for (int cycle = 0; cycle < AppearanceCycles; cycle++)
        {
            ISpikeAppearanceSource source = CreateAppearanceSource();
            try
            {
                bool isDark = source.IsDark;
                if (observedDark is not null && observedDark != isDark)
                {
                    throw new InvalidOperationException("The system appearance changed during the measurement run.");
                }

                observedDark = isDark;
                source.InvokeSynthetic();
                if (source.NotificationCount != 1)
                {
                    throw new InvalidOperationException("The synthetic appearance callback was not delivered exactly once.");
                }

                source.SuppressCallbacks();
                source.InvokeSynthetic();
                if (source.NotificationCount != 1)
                {
                    throw new InvalidOperationException("A logically disposed appearance callback was delivered.");
                }
            }
            finally
            {
                source.Dispose();
                source.Dispose();
            }
        }

#if NARROW_ABI
        if (RawColorValuesChangedHandler.ActiveInstances != 0)
        {
            throw new InvalidOperationException("A raw WinRT callback instance remained active after the stress loop.");
        }
#endif
        return observedDark ?? throw new InvalidOperationException("The appearance stress loop did not execute.");
    }

#if NARROW_ABI
    private static void RunNarrowFailureCleanupSelfTest()
    {
        foreach (NarrowFailureCheckpoint checkpoint in Enum.GetValues<NarrowFailureCheckpoint>()[1..])
        {
            try
            {
                _ = new NarrowAbiAppearanceSource(checkpoint);
                throw new InvalidOperationException($"Failure checkpoint {checkpoint} did not fail.");
            }
            catch (NarrowFailureInjectionException)
            {
            }

            if (RawColorValuesChangedHandler.ActiveInstances != 0)
            {
                throw new InvalidOperationException($"Failure checkpoint {checkpoint} leaked a callback instance.");
            }
        }
    }
#endif

    // Both compile modes intentionally retain one interface-shaped harness so their managed call graph stays comparable.
#pragma warning disable CA1859
    private static ISpikeAppearanceSource CreateAppearanceSource()
    {
#if SDK_PROJECTION
        return new SdkProjectionAppearanceSource();
#elif NARROW_ABI
        return new NarrowAbiAppearanceSource();
#else
#error An appearance interop mode must be selected.
#endif
    }
#pragma warning restore CA1859

    private static string GetMode()
    {
#if SDK_PROJECTION
        return "SdkProjection";
#elif NARROW_ABI
        return "NarrowAbi";
#else
#error An appearance interop mode must be selected.
#endif
    }

    private static void RunHiddenWebView(string userDataDirectory)
    {
        _window = PInvoke.CreateWindowEx(
            default,
            "STATIC",
            "Nanto WinRT ABI spike",
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
            0,
            0,
            320,
            240,
            default,
            null,
            null,
            null);
        if (_window.IsNull)
        {
            throw new InvalidOperationException("The hidden native window could not be created.");
        }

        void* handler = ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.ConvertToUnmanaged(
            _environmentCreatedHandler);
        try
        {
            ThrowIfFailed(WebView2Loader.CreateCoreWebView2EnvironmentWithOptions(null, userDataDirectory, 0, (nint)handler));
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.Free(handler);
        }

        while (PInvoke.GetMessage(out MSG message, default, 0, 0).Value > 0)
        {
            _ = PInvoke.TranslateMessage(in message);
            _ = PInvoke.DispatchMessage(in message);
        }

        if (_exitCode != 0)
        {
            throw new InvalidOperationException("The hidden WebView2 smoke test failed.");
        }
    }

    private static T Adopt<T>(nint pointer)
        where T : class
    {
        if (pointer == 0)
        {
            throw new InvalidOperationException("A native callback returned a null COM interface.");
        }

        return UniqueComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer)
            ?? throw new InvalidOperationException("A native COM interface could not be adopted.");
    }

    private static void CleanupWebView()
    {
        if (_webView is not null && _navigationToken.Value != 0)
        {
            _ = _webView.remove_NavigationCompleted(_navigationToken);
        }

        if (_controller is not null)
        {
            _ = _controller.Close();
        }

        Release(ref _webView);
        Release(ref _controller);
        Release(ref _environment);
        if (!_window.IsNull)
        {
            _ = PInvoke.DestroyWindow(_window);
            _window = default;
        }
    }

    private static void Release<T>(ref T? value)
        where T : class
    {
        T? released = value;
        value = null;
        if (released is not null)
        {
            ((ComObject)(object)released).FinalRelease();
        }
    }

    private static void ThrowIfFailed(HRESULT result)
    {
        ThrowIfFailed(result.Value);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }
}

[GeneratedComClass]
internal sealed partial class EnvironmentCreatedHandler : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    public int Invoke(int errorCode, nint createdEnvironment) => Program.EnvironmentCreated(errorCode, createdEnvironment);
}

[GeneratedComClass]
internal sealed partial class ControllerCreatedHandler : ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
    public int Invoke(int errorCode, nint createdController) => Program.ControllerCreated(errorCode, createdController);
}

[GeneratedComClass]
internal sealed partial class NavigationCompletedHandler : ICoreWebView2NavigationCompletedEventHandler
{
    public int Invoke(nint sender, nint args) => Program.NavigationCompleted();
}