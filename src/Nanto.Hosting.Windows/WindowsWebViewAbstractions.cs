using Nanto.Hosting;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal interface IWindowsWebViewApplicationFactory
{
    ValueTask<IWindowsWebViewApplication> CreateAsync(
        ValidatedApplicationOptions options,
        IUiDispatcher dispatcher,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);
}

internal interface IWindowsWebViewApplication : IAsyncDisposable
{
    ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
        HWND parentWindow,
        WindowId windowId,
        WindowOptions options,
        ColorSchemePreference preferredColorScheme,
        Action<RendererFailureKind, string, bool> reportRendererFailure,
        Action requestClose,
        Func<bool> canRecoverRenderer,
        CancellationToken cancellationToken);

    ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken);

    ValueTask WaitForReadinessAsync(CancellationToken cancellationToken);

    ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken);

    void CrashRendererForTesting() => throw new NotSupportedException("This WebView application does not expose the renderer-crash test seam.");

    uint GetBrowserProcessIdForTesting() => throw new NotSupportedException("This WebView application does not expose the browser-process test seam.");
}

internal interface IWindowsWebViewWindow : IAsyncDisposable
{
    Task Readiness { get; }

    void MoveFocus();

    void SetBounds(int width, int height);
}

internal sealed class NoOpWindowsWebViewApplicationFactory : IWindowsWebViewApplicationFactory
{
    public static NoOpWindowsWebViewApplicationFactory Instance { get; } = new();

    private NoOpWindowsWebViewApplicationFactory()
    {
    }

    public ValueTask<IWindowsWebViewApplication> CreateAsync(
        ValidatedApplicationOptions options,
        IUiDispatcher dispatcher,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IWindowsWebViewApplication>(NoOpWindowsWebViewApplication.Instance);
    }

    private sealed class NoOpWindowsWebViewApplication : IWindowsWebViewApplication
    {
        public static NoOpWindowsWebViewApplication Instance { get; } = new();

        public ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            HWND parentWindow,
            WindowId windowId,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            Action<RendererFailureKind, string, bool> reportRendererFailure,
            Action requestClose,
            Func<bool> canRecoverRenderer,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(reportRendererFailure);
            ArgumentNullException.ThrowIfNull(requestClose);
            ArgumentNullException.ThrowIfNull(canRecoverRenderer);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IWindowsWebViewWindow>(NoOpWindowsWebViewWindow.Instance);
        }

        public ValueTask SetPreferredColorSchemeAsync(
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask WaitForReadinessAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<string>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));

        public void CrashRendererForTesting() => throw new NotSupportedException("The no-op WebView cannot crash a renderer.");

        public uint GetBrowserProcessIdForTesting() => throw new NotSupportedException("The no-op WebView does not have a browser process.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpWindowsWebViewWindow : IWindowsWebViewWindow
    {
        public static NoOpWindowsWebViewWindow Instance { get; } = new();

        public Task Readiness => Task.CompletedTask;

        public void MoveFocus()
        {
        }

        public void SetBounds(int width, int height)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
