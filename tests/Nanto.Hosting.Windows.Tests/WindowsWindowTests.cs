using AwesomeAssertions;

using Windows.Win32;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowsWindowTests
{
    [Fact]
    public async Task HiddenWindowPublishesMutationsAndClosesAfterACanceledCallerStopsWaiting()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        Win32WindowClass? windowClass = null;
        WindowsWindow? window = null;
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                async _ =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = await CreateHiddenWindowAsync(windowClass, ledger, dispatcher);
                },
                TestContext.Current.CancellationToken);

            window!.State.Should().Be(WindowState.Running);
            window.IsVisible.Should().BeFalse();

            var updatedSize = new WindowSize(720, 520);
            await window.SetTitleAsync("Updated title", TestContext.Current.CancellationToken);
            await window.SetSizeAsync(updatedSize, TestContext.Current.CancellationToken);
            window.Title.Should().Be("Updated title");
            window.Size.Width.Should().BeApproximately(updatedSize.Width, 1);
            window.Size.Height.Should().BeApproximately(updatedSize.Height, 1);

            using var canceledWait = new CancellationTokenSource();
            canceledWait.Cancel();
            var closeWait = window.CloseAsync(canceledWait.Token);
            await closeWait.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
            await window.CloseAsync(TestContext.Current.CancellationToken);

            window.State.Should().Be(WindowState.Closed);
            window.IsVisible.Should().BeFalse();
            await dispatcher.InvokeAsync(windowClass!.Dispose, TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task NativeCloseRaisesClosingAndClosedOnce()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        Win32WindowClass? windowClass = null;
        WindowsWindow? window = null;
        var states = new List<WindowState>();
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                async _ =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = await CreateHiddenWindowAsync(windowClass, ledger, dispatcher);
                    window.StateChanged += (_, eventArgs) => states.Add(eventArgs.NewState);
                },
                TestContext.Current.CancellationToken);

            ((bool)PInvoke.PostMessage(window!.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
            await window.CloseAsync(TestContext.Current.CancellationToken);

            states.Should().Equal(WindowState.Closing, WindowState.Closed);
            await dispatcher.InvokeAsync(windowClass!.Dispose, TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task StateHandlerFailureDoesNotSuppressLaterHandlersOrCleanup()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        Win32WindowClass? windowClass = null;
        WindowsWindow? window = null;
        var laterHandlerCount = 0;
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                async _ =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = await CreateHiddenWindowAsync(windowClass, ledger, dispatcher);
                    window.StateChanged += static (_, _) => throw new InvalidOperationException("handler failed");
                    window.StateChanged += (_, _) => laterHandlerCount++;
                },
                TestContext.Current.CancellationToken);

            await window!.CloseAsync(TestContext.Current.CancellationToken);

            laterHandlerCount.Should().Be(2);
            await dispatcher.InvokeAsync(windowClass!.Dispose, TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task UnrepresentableSizeFailsBeforeNativeMutation()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        Win32WindowClass? windowClass = null;
        WindowsWindow? window = null;
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                async _ =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = await CreateHiddenWindowAsync(windowClass, ledger, dispatcher);
                },
                TestContext.Current.CancellationToken);

            var setSize = async () => await window!.SetSizeAsync(
                new WindowSize((double)int.MaxValue + 1, 100),
                TestContext.Current.CancellationToken);

            await setSize.Should().ThrowAsync<ArgumentOutOfRangeException>();
            await window!.DisposeAsync();
            await dispatcher.InvokeAsync(windowClass!.Dispose, TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task PartialCreationAggregatesTheCreationAndCleanupFailures()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        Win32WindowClass? windowClass = null;
        var cleanupFailure = new InvalidOperationException("WebView cleanup failed");
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var creation = () => dispatcher.InvokeAsync(
                async _ =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    await WindowsWindow.CreateAsync(
                        windowClass,
                        ledger,
                        dispatcher,
                        new CancelingWebViewApplication(cancellation, cleanupFailure),
                        new WindowOptions { Title = "Partial creation" },
                        ColorSchemePreference.System,
                        cancellationToken: cancellation.Token);
                },
                TestContext.Current.CancellationToken).AsTask();

            var exception = (await creation.Should().ThrowAsync<AggregateException>()).Which;
            exception.Flatten().InnerExceptions.Should().ContainSingle(static failure => failure is OperationCanceledException);
            exception.Flatten().InnerExceptions.Should().Contain(cleanupFailure);
            await dispatcher.InvokeAsync(windowClass!.Dispose, TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    private static async ValueTask<WindowsWindow> CreateHiddenWindowAsync(
        Win32WindowClass windowClass,
        ResourceLedger ledger,
        IUiDispatcher dispatcher)
    {
        var options = new WindowOptions
        {
            Title = "Nanto hidden production window",
            StartVisible = false,
        };
        var validatedOptions = Nanto.Hosting.ValidatedApplicationOptions.Create(new NantoApplicationOptions
        {
            ApplicationId = "com.example.nanto-window-tests",
            Assets = new UnusedAssetProvider(),
            PrimaryWindow = options,
        });
        var webViewApplication = await NoOpWindowsWebViewApplicationFactory.Instance.CreateAsync(
            validatedOptions,
            ledger,
            NoOpPhase1FailureInjector.Instance,
            TestContext.Current.CancellationToken);
        return await WindowsWindow.CreateAsync(
            windowClass,
            ledger,
            dispatcher,
            webViewApplication,
            options,
            ColorSchemePreference.System,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class UnusedAssetProvider : IWebAssetProvider
    {
        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancelingWebViewApplication(CancellationTokenSource cancellation, Exception cleanupFailure) : IWindowsWebViewApplication
    {
        public ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            global::Windows.Win32.Foundation.HWND parentWindow,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromResult<IWindowsWebViewWindow>(new FailingCleanupWebViewWindow(cleanupFailure));
        }

        public ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WaitForReadinessAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<string>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingCleanupWebViewWindow(Exception cleanupFailure) : IWindowsWebViewWindow
    {
        public Task Readiness => Task.CompletedTask;

        public void SetBounds(int width, int height)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.FromException(cleanupFailure);
    }
}
