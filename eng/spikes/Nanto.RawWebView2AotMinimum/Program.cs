using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Nanto.RawWebView2AotMinimum.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.RawWebView2AotMinimum;

internal static unsafe class Program
{
    private const string Html = "<!doctype html><title>Nanto</title><h1>Hello</h1>";

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
        if (args.Length != 1)
        {
            return 2;
        }

        ThrowIfFailed(PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED));
        try
        {
            _window = PInvoke.CreateWindowEx(
                default,
                "STATIC",
                "Raw WebView2 AOT minimum",
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
                return 1;
            }

            void* handler = ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.ConvertToUnmanaged(
                _environmentCreatedHandler);
            try
            {
                ThrowIfFailed(WebView2Loader.CreateCoreWebView2EnvironmentWithOptions(null, args[0], 0, (nint)handler));
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

            return _exitCode;
        }
        finally
        {
            Cleanup();
            PInvoke.CoUninitialize();
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
            ThrowIfFailed(_controller.put_IsVisible(1));

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

            fixed (char* html = Html)
            {
                ThrowIfFailed(_webView.NavigateToString((nint)html));
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

    private static T Adopt<T>(nint pointer)
        where T : class
    {
        if (pointer == 0)
        {
            throw new InvalidOperationException();
        }

        return UniqueComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer) ?? throw new InvalidOperationException();
    }

    private static void Cleanup()
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