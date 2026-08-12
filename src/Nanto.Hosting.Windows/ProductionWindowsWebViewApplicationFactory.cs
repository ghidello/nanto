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
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
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
            return new ProductionWindowsWebViewApplication(
                environment,
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
        private readonly WebView2EnvironmentOwner _environment;
        private readonly IPhase1FailureInjector _failureInjector;
        private readonly ResourceLedger _resourceLedger;
        private IWebAssetLease? _assetLease;
        private IDisposable? _assetResourceLease;
        private WebView2WindowHost? _window;

        public ProductionWindowsWebViewApplication(
            WebView2EnvironmentOwner environment,
            IWebAssetLease assetLease,
            IDisposable assetResourceLease,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector)
        {
            _environment = environment;
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

            var window = await WebView2WindowHost.CreateAsync(
                _environment,
                parentWindow,
                options,
                preferredColorScheme,
                _assetLease,
                _resourceLedger,
                _failureInjector,
                cancellationToken);
            _window = window;
            return window;
        }

        public ValueTask SetPreferredColorSchemeAsync(
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken) =>
            _window?.SetPreferredColorSchemeAsync(preferredColorScheme, cancellationToken)
                ?? throw new InvalidOperationException("The WebView2 window is not available.");

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

}