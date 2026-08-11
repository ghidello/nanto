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
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(windowClass, ledger, dispatcher);
                },
                TestContext.Current.CancellationToken);

            window!.State.Should().Be(WindowState.Running);
            window.IsVisible.Should().BeFalse();

            var updatedBounds = new WindowBounds(-120, 80, 720, 520);
            await window.SetTitleAsync("Updated title", TestContext.Current.CancellationToken);
            await window.SetBoundsAsync(updatedBounds, TestContext.Current.CancellationToken);
            window.Title.Should().Be("Updated title");
            window.Bounds.Should().Be(updatedBounds);

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
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(windowClass, ledger, dispatcher);
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
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(windowClass, ledger, dispatcher);
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
    public async Task UnrepresentableBoundsFailBeforeNativeMutation()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        Win32WindowClass? windowClass = null;
        WindowsWindow? window = null;
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(windowClass, ledger, dispatcher);
                },
                TestContext.Current.CancellationToken);

            var setBounds = () => window!.SetBoundsAsync(
                new WindowBounds((double)int.MaxValue + 1, 0, 100, 100),
                TestContext.Current.CancellationToken);

            setBounds.Should().Throw<ArgumentOutOfRangeException>();
            await window!.DisposeAsync();
            await dispatcher.InvokeAsync(windowClass!.Dispose, TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    private static WindowsWindow CreateHiddenWindow(Win32WindowClass windowClass, ResourceLedger ledger, IUiDispatcher dispatcher) => new(
        windowClass,
        ledger,
        dispatcher,
        new WindowOptions
        {
            Title = "Nanto hidden production window",
            StartVisible = false,
        });
}
