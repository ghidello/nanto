using System.Runtime.InteropServices.Marshalling;

using Microsoft.Extensions.Logging;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal sealed class WebView2WindowHost : IWindowsWebViewWindow
{
    private static readonly int _browserProcessUnavailableHResult =
        HResult.FromWin32(WIN32_ERROR.ERROR_INVALID_STATE);

    private readonly NavigationCompletedHandler _navigationCompletedHandler;
    private readonly NavigationStartingHandler _navigationStartingHandler;
    private readonly ProcessFailedHandler _processFailedHandler;
    private readonly RendererRecoveryCoordinator _rendererRecovery;
    private readonly WebMessageReceivedHandler _webMessageReceivedHandler;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private bool _browserProcessExited;
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
    private EventRegistrationToken _processFailedToken;
    private IDisposable? _processFailedSubscriptionLease;
    private UniqueComReference<ICoreWebView2Settings>? _settings;
    private IDisposable? _settingsResourceLease;
    private UniqueComReference<ICoreWebView2>? _webView;
    private IDisposable? _webViewResourceLease;
    private EventRegistrationToken _webMessageReceivedToken;
    private IDisposable? _webMessageSubscriptionLease;

    public Task Readiness => _webMessageReceivedHandler.Readiness;

    private WebView2WindowHost(
        IReadOnlySet<string> assetPaths,
        Action<RendererFailureKind, string, bool> reportRendererFailure,
        Action requestClose,
        Func<bool> canRecoverRenderer,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        WindowId windowId)
    {
        _logger = loggerFactory.CreateLogger<WebView2WindowHost>();
        _timeProvider = timeProvider;
        _navigationStartingHandler = new NavigationStartingHandler(assetPaths);
        _rendererRecovery = new RendererRecoveryCoordinator(
            reportRendererFailure,
            Reload,
            requestClose,
            canRecoverRenderer,
            loggerFactory,
            timeProvider,
            windowId);
        _navigationCompletedHandler = new NavigationCompletedHandler { NavigationCompleted = HandleNavigationCompleted };
        _processFailedHandler = new ProcessFailedHandler(HandleProcessFailed);
        _webMessageReceivedHandler = new WebMessageReceivedHandler();
    }

    public static async ValueTask<WebView2WindowHost> CreateAsync(
        WebView2EnvironmentOwner environment,
        HWND parentWindow,
        WindowId windowId,
        WindowOptions options,
        ColorSchemePreference preferredColorScheme,
        IWebAssetLease assetLease,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        Action<RendererFailureKind, string, bool> reportRendererFailure,
        Action requestClose,
        Func<bool> canRecoverRenderer,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assetLease);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        ArgumentNullException.ThrowIfNull(reportRendererFailure);
        ArgumentNullException.ThrowIfNull(requestClose);
        ArgumentNullException.ThrowIfNull(canRecoverRenderer);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();

        var host = new WebView2WindowHost(
            assetLease.AssetPaths,
            reportRendererFailure,
            requestClose,
            canRecoverRenderer,
            loggerFactory,
            timeProvider,
            windowId);
        var creationStartedAt = timeProvider.GetTimestamp();
        try
        {
            var operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "Controller");
            host._controller = await environment.CreateControllerAsync(parentWindow, cancellationToken);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "Controller",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            host._controllerResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "WebView2Controller");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewControllerCreated);
            cancellationToken.ThrowIfCancellationRequested();

            operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "WebView");
            host._webView = GetWebView(host._controller.Value);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "WebView",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            host._webViewResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "CoreWebView2");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewCreated);
            cancellationToken.ThrowIfCancellationRequested();

            var profileSource = (ICoreWebView2_13)host._webView.Value;
            operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "Profile");
            host._profile = GetProfile(profileSource);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "Profile",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            host._profileResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "WebView2Profile");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewProfileCreated);
            cancellationToken.ThrowIfCancellationRequested();
            host.SetPreferredColorScheme(preferredColorScheme, NantoFailureStage.Startup);

            host.ConfigureController(parentWindow);
            operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "Settings");
            host.AcquireAndConfigureSettings(resourceLedger, failureInjector, cancellationToken);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "Settings",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewSettingsConfigured);
            cancellationToken.ThrowIfCancellationRequested();

            operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "Subscriptions");
            host.AddNavigationStartingSubscription(resourceLedger);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.NavigationStartingSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            host.AddNavigationCompletedSubscription(resourceLedger);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.NavigationCompletedSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            host.AddMessageSubscription(resourceLedger);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebMessageSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            host.AddProcessFailedSubscription(resourceLedger);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "Subscriptions",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.ProcessFailedSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();
            operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "Mapping");
            host.AddVirtualHostMapping(resourceLedger, assetLease.RootDirectory);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "Mapping",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.VirtualHostMappingAdded);
            cancellationToken.ThrowIfCancellationRequested();

            operationStartedAt = timeProvider.GetTimestamp();
            WindowsDiagnostics.WebViewAcquisitionStarted(host._logger, "InitialNavigation");
            using var initialUri = new Utf16String($"https://{NavigationPolicy.ApplicationHostName}{options.InitialRoute}");
            HResult.ThrowIfFailed(host._webView.Value.Navigate(initialUri.Pointer), "webview2.navigation.begin", NantoFailureStage.Startup);
            await host._navigationCompletedHandler.Completion.WaitAsync(cancellationToken);
            WindowsDiagnostics.WebViewAcquisitionCompleted(
                host._logger,
                "InitialNavigation",
                timeProvider.GetElapsedTime(operationStartedAt).TotalMilliseconds);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.InitialNavigationCompleted);
            cancellationToken.ThrowIfCancellationRequested();
            WindowsDiagnostics.WebViewRunning(
                host._logger,
                timeProvider.GetElapsedTime(creationStartedAt).TotalMilliseconds);
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

    public void MoveFocus()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        HResult.ThrowIfFailed(
            _controller!.Value.MoveFocus((int)COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC),
            "webview2.controller.move-focus",
            NantoFailureStage.Runtime);
    }

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

    public unsafe void CrashRendererForTesting()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var methodName = new Utf16String("Page.crash");
        using var parameters = new Utf16String("{}");
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>.ConvertToUnmanaged(
            TestDevToolsProtocolMethodCompletedHandler.Instance);
        try
        {
            HResult.ThrowIfFailed(
                _webView!.Value.CallDevToolsProtocolMethod(methodName.Pointer, parameters.Pointer, (nint)handlerPointer),
                "webview2.test.crash-renderer",
                NantoFailureStage.Runtime);
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>.Free(handlerPointer);
        }
    }

    public unsafe uint GetBrowserProcessIdForTesting()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        uint processId = 0;
        HResult.ThrowIfFailed(
            _webView!.Value.get_BrowserProcessId((nint)(&processId)),
            "webview2.test.get-browser-process-id",
            NantoFailureStage.Runtime);
        return processId;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        List<Exception>? failures = null;
        TryCleanup(RemoveProcessFailedSubscription, ref failures);
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

    private unsafe void AddProcessFailedSubscription(ResourceLedger resourceLedger)
    {
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2ProcessFailedEventHandler>.ConvertToUnmanaged(_processFailedHandler);
        try
        {
            var token = default(EventRegistrationToken);
            HResult.ThrowIfFailed(
                _webView!.Value.add_ProcessFailed((nint)handlerPointer, (nint)(&token)),
                "webview2.process-failed.subscribe",
                NantoFailureStage.Startup);
            _processFailedToken = token;
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2ProcessFailedEventHandler>.Free(handlerPointer);
        }

        _processFailedSubscriptionLease = resourceLedger.Acquire(WindowsResourceKind.Subscription, "ProcessFailedSubscription");
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
        if (_controller is not null && !_browserProcessExited)
        {
            HandleWebViewTeardownResult(_controller.Value.Close(), "webview2.controller.close");
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
            if (!_browserProcessExited)
            {
                HandleWebViewTeardownResult(
                    _webView!.Value.remove_WebMessageReceived(_webMessageReceivedToken),
                    "webview2.message.unsubscribe");
            }
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
            if (!_browserProcessExited)
            {
                HandleWebViewTeardownResult(
                    _webView!.Value.remove_NavigationCompleted(_navigationCompletedToken),
                    "webview2.navigation.unsubscribe");
            }
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
            if (!_browserProcessExited)
            {
                HandleWebViewTeardownResult(
                    _webView!.Value.remove_NavigationStarting(_navigationStartingToken),
                    "webview2.navigation-starting.unsubscribe");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _navigationStartingSubscriptionLease, null)?.Dispose();
        }
    }

    private void RemoveProcessFailedSubscription()
    {
        if (_processFailedSubscriptionLease is null)
        {
            return;
        }

        try
        {
            if (!_browserProcessExited)
            {
                HandleWebViewTeardownResult(
                    _webView!.Value.remove_ProcessFailed(_processFailedToken),
                    "webview2.process-failed.unsubscribe");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _processFailedSubscriptionLease, null)?.Dispose();
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
            if (!_browserProcessExited)
            {
                using var hostName = new Utf16String(NavigationPolicy.ApplicationHostName);
                HandleWebViewTeardownResult(
                    ((ICoreWebView2_3)_webView!.Value).ClearVirtualHostNameToFolderMapping(hostName.Pointer),
                    "webview2.mapping.remove");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _mappingResourceLease, null)?.Dispose();
        }
    }

    private void SetPreferredColorScheme(ColorSchemePreference preferredColorScheme, NantoFailureStage failureStage)
    {
        var startedAt = _timeProvider.GetTimestamp();
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
        WindowsDiagnostics.AppearanceApplied(
            _logger,
            preferredColorScheme,
            failureStage,
            _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private void HandleNavigationCompleted(bool succeeded)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _rendererRecovery.HandleNavigationCompleted(succeeded);
        }
    }

    private void HandleWebViewTeardownResult(int result, string operation)
    {
        if (result == _browserProcessUnavailableHResult)
        {
            _browserProcessExited = true;
            WindowsDiagnostics.BrowserOperationUnavailableDuringTeardown(_logger, operation);
            return;
        }

        HResult.ThrowIfFailed(result, operation, NantoFailureStage.Teardown);
    }

    private void HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND failureKind)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            if (failureKind is COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED)
            {
                _browserProcessExited = true;
            }

            _rendererRecovery.HandleProcessFailed(failureKind);
        }
    }

    private void Reload() => HResult.ThrowIfFailed(_webView!.Value.Reload(), "webview2.renderer.reload", NantoFailureStage.Runtime);

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
