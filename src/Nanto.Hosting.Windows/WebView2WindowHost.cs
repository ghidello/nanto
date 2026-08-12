using System.Runtime.InteropServices.Marshalling;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal sealed class WebView2WindowHost : IWindowsWebViewWindow
{
    private readonly NavigationCompletedHandler _navigationCompletedHandler;
    private readonly NavigationStartingHandler _navigationStartingHandler;
    private readonly WebMessageReceivedHandler _webMessageReceivedHandler;
    private UniqueComReference<ICoreWebView2Controller>? _controller;
    private IDisposable? _controllerResourceLease;
    private int _disposed;
    private IDisposable? _mappingResourceLease;
    private EventRegistrationToken _navigationCompletedToken;
    private IDisposable? _navigationSubscriptionLease;
    private EventRegistrationToken _navigationStartingToken;
    private IDisposable? _navigationStartingSubscriptionLease;
    private UniqueComReference<ICoreWebView2Profile>? _profile;
    private IDisposable? _profileResourceLease;
    private UniqueComReference<ICoreWebView2Settings>? _settings;
    private IDisposable? _settingsResourceLease;
    private UniqueComReference<ICoreWebView2>? _webView;
    private IDisposable? _webViewResourceLease;
    private EventRegistrationToken _webMessageReceivedToken;
    private IDisposable? _webMessageSubscriptionLease;

    public Task Readiness => _webMessageReceivedHandler.Readiness;

    private WebView2WindowHost(
        NavigationStartingHandler navigationStartingHandler,
        NavigationCompletedHandler navigationCompletedHandler,
        WebMessageReceivedHandler webMessageReceivedHandler)
    {
        _navigationStartingHandler = navigationStartingHandler;
        _navigationCompletedHandler = navigationCompletedHandler;
        _webMessageReceivedHandler = webMessageReceivedHandler;
    }

    public static async ValueTask<WebView2WindowHost> CreateAsync(
        WebView2EnvironmentOwner environment,
        HWND parentWindow,
        WindowOptions options,
        ColorSchemePreference preferredColorScheme,
        IWebAssetLease assetLease,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assetLease);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        cancellationToken.ThrowIfCancellationRequested();

        var navigationStartingHandler = new NavigationStartingHandler(assetLease.AssetPaths);
        var navigationHandler = new NavigationCompletedHandler();
        var messageHandler = new WebMessageReceivedHandler();
        var host = new WebView2WindowHost(navigationStartingHandler, navigationHandler, messageHandler);
        try
        {
            host._controller = await environment.CreateControllerAsync(parentWindow, cancellationToken);
            host._controllerResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "WebView2Controller");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewControllerCreated);
            cancellationToken.ThrowIfCancellationRequested();

            host._webView = GetWebView(host._controller.Value);
            host._webViewResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "CoreWebView2");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewCreated);
            cancellationToken.ThrowIfCancellationRequested();

            var profileSource = (ICoreWebView2_13)host._webView.Value;
            host._profile = GetProfile(profileSource);
            host._profileResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "WebView2Profile");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewProfileCreated);
            cancellationToken.ThrowIfCancellationRequested();
            host.SetPreferredColorScheme(preferredColorScheme, NantoFailureStage.Startup);

            host.ConfigureController(parentWindow);
            host.AcquireAndConfigureSettings(resourceLedger, failureInjector, cancellationToken);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewSettingsConfigured);
            cancellationToken.ThrowIfCancellationRequested();

            host.AddNavigationStartingSubscription(resourceLedger);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.NavigationStartingSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            host.AddNavigationCompletedSubscription(resourceLedger);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.NavigationCompletedSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            host.AddMessageSubscription(resourceLedger);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebMessageSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            host.AddVirtualHostMapping(resourceLedger, assetLease.RootDirectory);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.VirtualHostMappingAdded);
            cancellationToken.ThrowIfCancellationRequested();

            using var initialUri = new Utf16String($"https://{NavigationPolicy.ApplicationHostName}{options.InitialRoute}");
            HResult.ThrowIfFailed(host._webView.Value.Navigate(initialUri.Pointer), "webview2.navigation.begin", NantoFailureStage.Startup);
            await navigationHandler.Completion.WaitAsync(cancellationToken);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.InitialNavigationCompleted);
            cancellationToken.ThrowIfCancellationRequested();
            return host;
        }
        catch (Exception creationException)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "WebView2 window creation failed and cleanup also failed.",
                    creationException,
                    cleanupException);
            }

            throw;
        }
    }

    public ValueTask SetPreferredColorSchemeAsync(
        ColorSchemePreference preferredColorScheme,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        SetPreferredColorScheme(preferredColorScheme, NantoFailureStage.Runtime);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
        _webMessageReceivedHandler.WaitForMessageAsync(cancellationToken);

    public void SetBounds(int width, int height)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        HResult.ThrowIfFailed(
            _controller!.Value.put_Bounds(new WebView2Rect(0, 0, width, height)),
            "webview2.controller.set-bounds",
            NantoFailureStage.Runtime);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        List<Exception>? failures = null;
        TryCleanup(RemoveMessageSubscription, ref failures);
        TryCleanup(RemoveNavigationCompletedSubscription, ref failures);
        TryCleanup(RemoveNavigationStartingSubscription, ref failures);
        TryCleanup(RemoveVirtualHostMapping, ref failures);
        TryCleanup(CloseController, ref failures);
        TryCleanup(() => _settings?.Dispose(), ref failures);
        _settings = null;
        TryCleanup(() => Interlocked.Exchange(ref _settingsResourceLease, null)?.Dispose(), ref failures);
        TryCleanup(() => _profile?.Dispose(), ref failures);
        _profile = null;
        TryCleanup(() => Interlocked.Exchange(ref _profileResourceLease, null)?.Dispose(), ref failures);
        TryCleanup(() => _webView?.Dispose(), ref failures);
        _webView = null;
        TryCleanup(() => Interlocked.Exchange(ref _webViewResourceLease, null)?.Dispose(), ref failures);
        TryCleanup(() => _controller?.Dispose(), ref failures);
        _controller = null;
        TryCleanup(() => Interlocked.Exchange(ref _controllerResourceLease, null)?.Dispose(), ref failures);
        return failures switch
        {
            null => ValueTask.CompletedTask,
            [var failure] => ValueTask.FromException(failure),
            _ => ValueTask.FromException(new AggregateException("WebView2 window cleanup encountered multiple failures.", failures)),
        };
    }

    private unsafe void AddMessageSubscription(ResourceLedger resourceLedger)
    {
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2WebMessageReceivedEventHandler>.ConvertToUnmanaged(_webMessageReceivedHandler);
        try
        {
            var token = default(EventRegistrationToken);
            HResult.ThrowIfFailed(
                _webView!.Value.add_WebMessageReceived((nint)handlerPointer, (nint)(&token)),
                "webview2.message.subscribe",
                NantoFailureStage.Startup);
            _webMessageReceivedToken = token;
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2WebMessageReceivedEventHandler>.Free(handlerPointer);
        }

        _webMessageSubscriptionLease = resourceLedger.Acquire(WindowsResourceKind.Subscription, "WebMessageReceivedSubscription");
    }

    private unsafe void AddNavigationCompletedSubscription(ResourceLedger resourceLedger)
    {
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2NavigationCompletedEventHandler>.ConvertToUnmanaged(_navigationCompletedHandler);
        try
        {
            var token = default(EventRegistrationToken);
            HResult.ThrowIfFailed(
                _webView!.Value.add_NavigationCompleted((nint)handlerPointer, (nint)(&token)),
                "webview2.navigation.subscribe",
                NantoFailureStage.Startup);
            _navigationCompletedToken = token;
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2NavigationCompletedEventHandler>.Free(handlerPointer);
        }

        _navigationSubscriptionLease = resourceLedger.Acquire(WindowsResourceKind.Subscription, "NavigationCompletedSubscription");
    }

    private unsafe void AddNavigationStartingSubscription(ResourceLedger resourceLedger)
    {
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2NavigationStartingEventHandler>.ConvertToUnmanaged(_navigationStartingHandler);
        try
        {
            var token = default(EventRegistrationToken);
            HResult.ThrowIfFailed(
                _webView!.Value.add_NavigationStarting((nint)handlerPointer, (nint)(&token)),
                "webview2.navigation-starting.subscribe",
                NantoFailureStage.Startup);
            _navigationStartingToken = token;
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2NavigationStartingEventHandler>.Free(handlerPointer);
        }

        _navigationStartingSubscriptionLease = resourceLedger.Acquire(WindowsResourceKind.Subscription, "NavigationStartingSubscription");
    }

    private void AddVirtualHostMapping(ResourceLedger resourceLedger, string rootDirectory)
    {
        using var hostName = new Utf16String(NavigationPolicy.ApplicationHostName);
        using var folderPath = new Utf16String(rootDirectory);
        var mappingWebView = (ICoreWebView2_3)_webView!.Value;
        HResult.ThrowIfFailed(
            mappingWebView.SetVirtualHostNameToFolderMapping(
                hostName.Pointer,
                folderPath.Pointer,
                (int)COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND.COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS),
            "webview2.mapping.add",
            NantoFailureStage.Startup);
        _mappingResourceLease = resourceLedger.Acquire(WindowsResourceKind.VirtualHostMapping, "ApplicationOriginMapping");
    }

    private void CloseController()
    {
        if (_controller is not null)
        {
            HResult.ThrowIfFailed(_controller.Value.Close(), "webview2.controller.close", NantoFailureStage.Teardown);
        }
    }

    private void ConfigureController(HWND parentWindow)
    {
        if (!PInvoke.GetClientRect(parentWindow, out var bounds))
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastPInvokeError(),
                "Nanto could not obtain the native window client bounds for WebView2.");
        }

        var webViewBounds = new WebView2Rect(bounds.left, bounds.top, bounds.right, bounds.bottom);
        HResult.ThrowIfFailed(_controller!.Value.put_Bounds(webViewBounds), "webview2.controller.set-bounds", NantoFailureStage.Startup);
        HResult.ThrowIfFailed(_controller.Value.put_IsVisible(1), "webview2.controller.set-visible", NantoFailureStage.Startup);
    }

    private unsafe void AcquireAndConfigureSettings(
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken)
    {
        nint settingsPointer = 0;
        HResult.ThrowIfFailed(_webView!.Value.get_Settings((nint)(&settingsPointer)), "webview2.settings.get", NantoFailureStage.Startup);
        _settings = UniqueComReference<ICoreWebView2Settings>.FromPointer(settingsPointer);
        _settingsResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "WebView2Settings");
        failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewSettingsCreated);
        cancellationToken.ThrowIfCancellationRequested();
        HResult.ThrowIfFailed(_settings.Value.put_IsWebMessageEnabled(1), "webview2.settings.enable-web-messages", NantoFailureStage.Startup);
        HResult.ThrowIfFailed(_settings.Value.put_AreHostObjectsAllowed(0), "webview2.settings.disable-host-objects", NantoFailureStage.Startup);
        HResult.ThrowIfFailed(_settings.Value.put_AreDevToolsEnabled(0), "webview2.settings.disable-dev-tools", NantoFailureStage.Startup);
        HResult.ThrowIfFailed(
            _settings.Value.put_AreDefaultContextMenusEnabled(0),
            "webview2.settings.disable-context-menus",
            NantoFailureStage.Startup);
        HResult.ThrowIfFailed(_settings.Value.put_IsStatusBarEnabled(0), "webview2.settings.disable-status-bar", NantoFailureStage.Startup);
    }

    private static unsafe UniqueComReference<ICoreWebView2> GetWebView(ICoreWebView2Controller controller)
    {
        nint webViewPointer = 0;
        HResult.ThrowIfFailed(controller.get_CoreWebView2((nint)(&webViewPointer)), "webview2.controller.get-webview", NantoFailureStage.Startup);
        return UniqueComReference<ICoreWebView2>.FromPointer(webViewPointer);
    }

    private static unsafe UniqueComReference<ICoreWebView2Profile> GetProfile(ICoreWebView2_13 profileSource)
    {
        nint profilePointer = 0;
        HResult.ThrowIfFailed(profileSource.get_Profile((nint)(&profilePointer)), "webview2.profile.get", NantoFailureStage.Startup);
        return UniqueComReference<ICoreWebView2Profile>.FromPointer(profilePointer);
    }

    private void RemoveMessageSubscription()
    {
        if (_webMessageSubscriptionLease is null)
        {
            return;
        }

        try
        {
            HResult.ThrowIfFailed(
                _webView!.Value.remove_WebMessageReceived(_webMessageReceivedToken),
                "webview2.message.unsubscribe",
                NantoFailureStage.Teardown);
        }
        finally
        {
            Interlocked.Exchange(ref _webMessageSubscriptionLease, null)?.Dispose();
        }
    }

    private void RemoveNavigationCompletedSubscription()
    {
        if (_navigationSubscriptionLease is null)
        {
            return;
        }

        try
        {
            HResult.ThrowIfFailed(
                _webView!.Value.remove_NavigationCompleted(_navigationCompletedToken),
                "webview2.navigation.unsubscribe",
                NantoFailureStage.Teardown);
        }
        finally
        {
            Interlocked.Exchange(ref _navigationSubscriptionLease, null)?.Dispose();
        }
    }

    private void RemoveNavigationStartingSubscription()
    {
        if (_navigationStartingSubscriptionLease is null)
        {
            return;
        }

        try
        {
            HResult.ThrowIfFailed(
                _webView!.Value.remove_NavigationStarting(_navigationStartingToken),
                "webview2.navigation-starting.unsubscribe",
                NantoFailureStage.Teardown);
        }
        finally
        {
            Interlocked.Exchange(ref _navigationStartingSubscriptionLease, null)?.Dispose();
        }
    }

    private void RemoveVirtualHostMapping()
    {
        if (_mappingResourceLease is null)
        {
            return;
        }

        try
        {
            using var hostName = new Utf16String(NavigationPolicy.ApplicationHostName);
            HResult.ThrowIfFailed(
                ((ICoreWebView2_3)_webView!.Value).ClearVirtualHostNameToFolderMapping(hostName.Pointer),
                "webview2.mapping.remove",
                NantoFailureStage.Teardown);
        }
        finally
        {
            Interlocked.Exchange(ref _mappingResourceLease, null)?.Dispose();
        }
    }

    private void SetPreferredColorScheme(ColorSchemePreference preferredColorScheme, NantoFailureStage failureStage)
    {
        var nativePreference = preferredColorScheme switch
        {
            ColorSchemePreference.System => COREWEBVIEW2_PREFERRED_COLOR_SCHEME.COREWEBVIEW2_PREFERRED_COLOR_SCHEME_AUTO,
            ColorSchemePreference.Light => COREWEBVIEW2_PREFERRED_COLOR_SCHEME.COREWEBVIEW2_PREFERRED_COLOR_SCHEME_LIGHT,
            ColorSchemePreference.Dark => COREWEBVIEW2_PREFERRED_COLOR_SCHEME.COREWEBVIEW2_PREFERRED_COLOR_SCHEME_DARK,
            _ => throw new ArgumentOutOfRangeException(nameof(preferredColorScheme), preferredColorScheme, "The preferred color scheme is not supported."),
        };
        HResult.ThrowIfFailed(
            _profile!.Value.put_PreferredColorScheme((int)nativePreference),
            "webview2.profile.set-color-scheme",
            failureStage);
    }

    private static void TryCleanup(Action cleanup, ref List<Exception>? failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures ??= [];
            failures.Add(exception);
        }
    }
}