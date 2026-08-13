using Nanto.Hosting;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal sealed class ProductionWindowsWebViewApplicationFactory : IWindowsWebViewApplicationFactory
{
    public static ProductionWindowsWebViewApplicationFactory Instance { get; } = new();

    private ProductionWindowsWebViewApplicationFactory()
    {
    }

    public async ValueTask<IWindowsWebViewApplication> CreateAsync(
        ValidatedApplicationOptions options,
        IUiDispatcher dispatcher,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        cancellationToken.ThrowIfCancellationRequested();

        var applicationCleanup = new AsyncCleanupRegistry();
        var environmentCleanup = new AsyncCleanupRegistry();
        IDisposable? assetResourceLease = null;
        try
        {
            var storage = await Task.Run(() => WindowsApplicationStorage.Prepare(options.Identity), cancellationToken);
            var assetLease = await options.Assets.PrepareAsync(
                options.CreateWebAssetPreparationContext(storage.ApplicationRoot),
                cancellationToken);
            applicationCleanup.Push("web-assets.dispose", () =>
            {
                DisposeAssetLease(assetLease, assetResourceLease);
                return ValueTask.CompletedTask;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (!NavigationPolicy.IsInitialRouteAllowed(options.PrimaryWindow.InitialRoute, assetLease.AssetPaths))
            {
                throw new InvalidDataException(
                    $"The initial route '{options.PrimaryWindow.InitialRoute}' does not resolve to a declared application asset.");
            }

            assetResourceLease = resourceLedger.Acquire(WindowsResourceKind.AssetLease, "WebAssetLease");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.AssetLeasePrepared);
            cancellationToken.ThrowIfCancellationRequested();
            var environment = await WebView2EnvironmentOwner.CreateAsync(
                storage.UserDataDirectory,
                resourceLedger,
                cancellationToken);
            environmentCleanup.Push("webview2.environment.dispose", environment.DisposeAsync);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.WebViewEnvironmentCreated);
            cancellationToken.ThrowIfCancellationRequested();
            var appearance = WindowsAppearanceManager.Create(
                dispatcher,
                resourceLedger,
                failureInjector,
                cancellationToken);
            applicationCleanup.Push("appearance.dispose", () =>
            {
                appearance.Dispose();
                return ValueTask.CompletedTask;
            });
            cancellationToken.ThrowIfCancellationRequested();
            return new ProductionWindowsWebViewApplication(
                environment,
                appearance,
                assetLease,
                assetResourceLease,
                resourceLedger,
                failureInjector);
        }
        catch (Exception creationException)
        {
            var cleanupExceptions = new List<Exception>();
            cleanupExceptions.AddRange(await applicationCleanup.DrainAsync());
            cleanupExceptions.AddRange(await environmentCleanup.DrainAsync());
            if (cleanupExceptions.Count != 0)
            {
                throw new AggregateException(
                    "WebView2 application creation failed and cleanup also failed.",
                    [creationException, .. cleanupExceptions]);
            }

            throw;
        }
    }

    private static void DisposeAssetLease(IWebAssetLease assetLease, IDisposable? resourceLease)
    {
        Exception? assetException = null;
        try
        {
            assetLease.Dispose();
        }
        catch (Exception exception)
        {
            assetException = exception;
        }

        try
        {
            resourceLease?.Dispose();
        }
        catch (Exception resourceException)
        {
            throw assetException is null
                ? resourceException
                : new AggregateException("The web-asset lease and its resource-ledger entry both failed to close.", assetException, resourceException);
        }

        if (assetException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(assetException).Throw();
        }
    }

    private static void AddCleanupException(ref List<Exception>? cleanupExceptions, Exception exception)
    {
        cleanupExceptions ??= [];
        cleanupExceptions.Add(exception);
    }

    private static void TryCleanup(Action cleanup, ref List<Exception>? cleanupExceptions)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            AddCleanupException(ref cleanupExceptions, exception);
        }
    }

    private sealed class ProductionWindowsWebViewApplication : IWindowsWebViewApplication
    {
        private readonly WindowsAppearanceManager _appearance;
        private readonly WebView2EnvironmentOwner _environment;
        private readonly IPhase1FailureInjector _failureInjector;
        private readonly ResourceLedger _resourceLedger;
        private IWebAssetLease? _assetLease;
        private IDisposable? _assetResourceLease;
        private ProductionWindowsWebViewWindow? _window;

        public ProductionWindowsWebViewApplication(
            WebView2EnvironmentOwner environment,
            WindowsAppearanceManager appearance,
            IWebAssetLease assetLease,
            IDisposable assetResourceLease,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector)
        {
            _environment = environment;
            _appearance = appearance;
            _assetLease = assetLease;
            _assetResourceLease = assetResourceLease;
            _resourceLedger = resourceLedger;
            _failureInjector = failureInjector;
        }

        public async ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            HWND parentWindow,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken)
        {
            if (_window is not null || _assetLease is null || _assetResourceLease is null)
            {
                throw new InvalidOperationException("The Phase 1 WebView2 application can create only one window.");
            }

            var appearanceAttachment = _appearance.AttachWindow(parentWindow, preferredColorScheme);
            try
            {
                var webViewWindow = await WebView2WindowHost.CreateAsync(
                    _environment,
                    parentWindow,
                    options,
                    preferredColorScheme,
                    _assetLease,
                    _resourceLedger,
                    _failureInjector,
                    cancellationToken);
                var window = new ProductionWindowsWebViewWindow(webViewWindow, appearanceAttachment);
                _window = window;
                return window;
            }
            catch (Exception creationException)
            {
                try
                {
                    appearanceAttachment.Dispose();
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "WebView2 window creation failed and its appearance registration also failed to close.",
                        creationException,
                        cleanupException);
                }

                throw;
            }
        }

        public async ValueTask SetPreferredColorSchemeAsync(
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = _window ?? throw new InvalidOperationException("The WebView2 window is not available.");
            var previousPreference = _appearance.PreferredColorScheme;
            _appearance.SetPreferredColorScheme(preferredColorScheme);
            try
            {
                await window.SetPreferredColorSchemeAsync(preferredColorScheme, cancellationToken);
            }
            catch (Exception mutationException)
            {
                try
                {
                    _appearance.SetPreferredColorScheme(previousPreference);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The WebView2 appearance mutation failed and native-frame rollback also failed.",
                        mutationException,
                        rollbackException);
                }

                throw;
            }
        }

        public ValueTask WaitForReadinessAsync(CancellationToken cancellationToken) =>
            _window is null
                ? throw new InvalidOperationException("The WebView2 window is not available.")
                : new ValueTask(_window.Readiness.WaitAsync(cancellationToken));

        public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
            _window?.WaitForDiagnosticMessageAsync(cancellationToken)
                ?? throw new InvalidOperationException("The WebView2 window is not available.");

        public async ValueTask DisposeAsync()
        {
            _window = null;
            List<Exception>? cleanupExceptions = null;
            TryCleanup(_appearance.Dispose, ref cleanupExceptions);
            var assetLease = _assetLease;
            _assetLease = null;
            TryCleanup(() => assetLease?.Dispose(), ref cleanupExceptions);
            TryCleanup(() => Interlocked.Exchange(ref _assetResourceLease, null)?.Dispose(), ref cleanupExceptions);
            try
            {
                await _environment.DisposeAsync();
            }
            catch (Exception exception)
            {
                AddCleanupException(ref cleanupExceptions, exception);
            }

            if (cleanupExceptions is [var cleanupException])
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
            }

            if (cleanupExceptions is { Count: > 1 })
            {
                throw new AggregateException("WebView2 application cleanup encountered multiple failures.", cleanupExceptions);
            }
        }
    }

    private sealed class ProductionWindowsWebViewWindow(
        WebView2WindowHost webViewWindow,
        IDisposable appearanceAttachment) : IWindowsWebViewWindow
    {
        private WebView2WindowHost? _webViewWindow = webViewWindow;
        private IDisposable? _appearanceAttachment = appearanceAttachment;

        public Task Readiness => _webViewWindow?.Readiness
            ?? throw new ObjectDisposedException(nameof(ProductionWindowsWebViewWindow));

        public async ValueTask DisposeAsync()
        {
            List<Exception>? cleanupExceptions = null;
            var window = Interlocked.Exchange(ref _webViewWindow, null);
            if (window is not null)
            {
                try
                {
                    await window.DisposeAsync();
                }
                catch (Exception exception)
                {
                    cleanupExceptions = [exception];
                }
            }

            TryCleanup(() => Interlocked.Exchange(ref _appearanceAttachment, null)?.Dispose(), ref cleanupExceptions);
            if (cleanupExceptions is [var cleanupException])
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
            }

            if (cleanupExceptions is { Count: > 1 })
            {
                throw new AggregateException("WebView2 window cleanup encountered multiple failures.", cleanupExceptions);
            }
        }

        public void SetBounds(int width, int height) =>
            (_webViewWindow ?? throw new ObjectDisposedException(nameof(ProductionWindowsWebViewWindow))).SetBounds(width, height);

        public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
            (_webViewWindow ?? throw new ObjectDisposedException(nameof(ProductionWindowsWebViewWindow)))
                .WaitForDiagnosticMessageAsync(cancellationToken);

        public ValueTask SetPreferredColorSchemeAsync(
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken) =>
            (_webViewWindow ?? throw new ObjectDisposedException(nameof(ProductionWindowsWebViewWindow)))
                .SetPreferredColorSchemeAsync(preferredColorScheme, cancellationToken);
    }

}