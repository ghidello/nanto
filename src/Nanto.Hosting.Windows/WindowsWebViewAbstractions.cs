using Nanto.Hosting;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal interface IWindowsWebViewApplicationFactory
{
    ValueTask<IWindowsWebViewApplication> CreateAsync(
        ValidatedApplicationOptions options,
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken);
}

internal interface IWindowsWebViewApplication : IAsyncDisposable
{
    ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
        HWND parentWindow,
        WindowOptions options,
        ColorSchemePreference preferredColorScheme,
        CancellationToken cancellationToken);

    ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken);

    ValueTask WaitForReadinessAsync(CancellationToken cancellationToken);

    ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken);
}

internal interface IWindowsWebViewWindow : IAsyncDisposable
{
    Task Readiness { get; }

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
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IWindowsWebViewApplication>(NoOpWindowsWebViewApplication.Instance);
    }

    private sealed class NoOpWindowsWebViewApplication : IWindowsWebViewApplication
    {
        public static NoOpWindowsWebViewApplication Instance { get; } = new();

        public ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            HWND parentWindow,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpWindowsWebViewWindow : IWindowsWebViewWindow
    {
        public static NoOpWindowsWebViewWindow Instance { get; } = new();

        public Task Readiness => Task.CompletedTask;

        public void SetBounds(int width, int height)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
