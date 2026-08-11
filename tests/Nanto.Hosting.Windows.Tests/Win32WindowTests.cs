using System.ComponentModel;

using AwesomeAssertions;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows.Tests;

public sealed class Win32WindowTests
{
    [Fact]
    public async Task HiddenWindowIsCreatedWithoutVisibilityOrActivationAndTearsDownInReverseOrder()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    using var window = CreateHiddenWindow(windowClass, ledger, dispatcher);

                    window.Handle.IsNull.Should().BeFalse();
                    ((bool)PInvoke.IsWindowVisible(window.Handle)).Should().BeFalse();
                    PInvoke.GetActiveWindow().Should().NotBe(window.Handle);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(1);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(2);

                    window.Dispose();
                    window.IsDestroyed.Should().BeTrue();
                    window.Dispose();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task NativeCloseDestroysTheWindowBeforeTheClassIsUnregistered()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            Win32WindowClass? windowClass = null;
            Win32Window? window = null;
            await dispatcher.InvokeAsync(
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(windowClass, ledger, dispatcher);
                },
                TestContext.Current.CancellationToken);

            ((bool)PInvoke.PostMessage(window!.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
            await dispatcher.InvokeAsync(
                () =>
                {
                    window.IsDestroyed.Should().BeTrue();
                    window.Dispose();
                    windowClass!.Dispose();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task CloseCallbackRunsOnceOnTheUiThreadAndDefersNativeDestruction()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            Win32WindowClass? windowClass = null;
            Win32Window? window = null;
            var closeRequestCount = 0;
            var closeRequestedOnUiThread = false;
            await dispatcher.InvokeAsync(
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        new Win32WindowCallbacks
                        {
                            CloseRequested = () =>
                            {
                                closeRequestCount++;
                                closeRequestedOnUiThread = dispatcher.CheckAccess();
                            },
                        });
                },
                TestContext.Current.CancellationToken);

            ((bool)PInvoke.PostMessage(window!.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
            ((bool)PInvoke.PostMessage(window.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
            await dispatcher.InvokeAsync(
                () =>
                {
                    closeRequestCount.Should().Be(1);
                    closeRequestedOnUiThread.Should().BeTrue();
                    window.IsDestroyed.Should().BeFalse();

                    window.Dispose();
                    windowClass!.Dispose();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task CloseCallbackFailureStillDestroysTheWindowAndIsReportedAfterCleanup()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            Win32WindowClass? windowClass = null;
            Win32Window? window = null;
            var callbackFailure = new InvalidOperationException("close callback failed");
            await dispatcher.InvokeAsync(
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        new Win32WindowCallbacks
                        {
                            CloseRequested = () => throw callbackFailure,
                        });
                },
                TestContext.Current.CancellationToken);

            ((bool)PInvoke.PostMessage(window!.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
            await dispatcher.InvokeAsync(
                () =>
                {
                    window.IsDestroyed.Should().BeTrue();
                    window.Invoking(static value => value.Dispose()).Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(callbackFailure);
                    windowClass!.Dispose();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task DestroyedCallbackRunsAfterNativeOwnershipIsReleased()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    var destroyedCount = 0;
                    using var window = CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        new Win32WindowCallbacks
                        {
                            Destroyed = () =>
                            {
                                destroyedCount++;
                                ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(0);
                                ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(1);
                            },
                        });

                    window.Dispose();
                    destroyedCount.Should().Be(1);
                    window.Dispose();
                    destroyedCount.Should().Be(1);
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task CallbackFailuresAreAggregatedAfterNativeOwnershipIsReleased()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            Win32WindowClass? windowClass = null;
            Win32Window? window = null;
            var closeFailure = new InvalidOperationException("close callback failed");
            var destroyedFailure = new InvalidOperationException("destroyed callback failed");
            await dispatcher.InvokeAsync(
                () =>
                {
                    windowClass = new Win32WindowClass(ledger, dispatcher);
                    window = CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        new Win32WindowCallbacks
                        {
                            CloseRequested = () => throw closeFailure,
                            Destroyed = () => throw destroyedFailure,
                        });
                },
                TestContext.Current.CancellationToken);

            ((bool)PInvoke.PostMessage(window!.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
            await dispatcher.InvokeAsync(
                () =>
                {
                    window.IsDestroyed.Should().BeTrue();
                    var callbackFailures = window.Invoking(static value => value.Dispose()).Should().Throw<AggregateException>().Which;
                    callbackFailures.InnerExceptions.Should().Equal(closeFailure, destroyedFailure);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(0);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(1);
                    windowClass!.Dispose();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task WindowClassCannotBeReleasedWhileItsWindowIsAliveButCanBeRetriedAfterDestruction()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    using var window = CreateHiddenWindow(windowClass, ledger, dispatcher);

                    windowClass.Invoking(static value => value.Dispose()).Should().Throw<Win32Exception>();
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(2);

                    window.Dispose();
                    windowClass.Dispose();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task CreationFailureReleasesTheWindowLedgerAcquisition()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    var windowClass = new Win32WindowClass(ledger, dispatcher);
                    windowClass.Dispose();

                    var create = () => CreateHiddenWindow(windowClass, ledger, dispatcher);

                    create.Should().Throw<ObjectDisposedException>();
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(0);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(0);
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task WindowClassCheckpointFailureRollsBackRegistration()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    var failure = new InvalidOperationException("window class checkpoint failed");
                    var create = () => new Win32WindowClass(
                        ledger,
                        dispatcher,
                        new DelegateFailureInjector(checkpoint =>
                        {
                            checkpoint.Should().Be(Phase1AcquisitionCheckpoint.WindowClassRegistered);
                            throw failure;
                        }));

                    create.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(0);
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task WindowCheckpointFailureDestroysTheWindowAndReleasesItsLedgerAcquisitions()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    var failure = new InvalidOperationException("window checkpoint failed");
                    var create = () => CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        failureInjector: new DelegateFailureInjector(checkpoint =>
                        {
                            checkpoint.Should().Be(Phase1AcquisitionCheckpoint.WindowCreated);
                            throw failure;
                        }));

                    create.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(0);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(1);
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task WindowCheckpointFailureRetainsDestroyedCallbackFailureAfterRollback()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    var creationFailure = new InvalidOperationException("window checkpoint failed");
                    var callbackFailure = new InvalidOperationException("destroyed callback failed");
                    var create = () => CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        new Win32WindowCallbacks
                        {
                            Destroyed = () => throw callbackFailure,
                        },
                        new DelegateFailureInjector(_ => throw creationFailure));

                    var failures = create.Should().Throw<AggregateException>().Which;
                    failures.InnerExceptions.Should().Equal(creationFailure, callbackFailure);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(0);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(1);
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task RollbackFailurePreservesWindowOwnershipUntilNativeDestructionIsConfirmed()
    {
        var ledger = new ResourceLedger();
        var uiThread = new WindowsUiThread(ledger);
        try
        {
            var dispatcher = await uiThread.DispatcherReady.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    using var windowClass = new Win32WindowClass(ledger, dispatcher);
                    HWND retainedHandle = default;
                    var create = () => CreateHiddenWindow(
                        windowClass,
                        ledger,
                        dispatcher,
                        failureInjector: new DelegateFailureInjector(static _ => throw new InvalidOperationException("window checkpoint failed")),
                        destroyWindow: handle =>
                        {
                            retainedHandle = handle;
                            return false;
                        });

                    create.Should().Throw<AggregateException>().Which.InnerExceptions.Should().HaveCount(2);
                    retainedHandle.IsNull.Should().BeFalse();
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(1);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(2);

                    ((bool)PInvoke.DestroyWindow(retainedHandle)).Should().BeTrue();
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.Window).Should().Be(0);
                    ledger.CaptureSnapshot().GetActiveCount(WindowsResourceKind.NativeHandle).Should().Be(1);
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await uiThread.DisposeAsync();
        }

        ledger.CaptureSnapshot().TotalActive.Should().Be(0);
    }

    private static Win32Window CreateHiddenWindow(
        Win32WindowClass windowClass,
        ResourceLedger ledger,
        IUiDispatcher dispatcher,
        Win32WindowCallbacks? callbacks = null,
        IPhase1FailureInjector? failureInjector = null,
        Func<HWND, bool>? destroyWindow = null) => new(
        windowClass,
        ledger,
        dispatcher,
        "Nanto hidden test window",
        100,
        100,
        640,
        480,
        WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
        WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
        callbacks,
        failureInjector,
        destroyWindow);

    private sealed class DelegateFailureInjector(Action<Phase1AcquisitionCheckpoint> onAcquired) : IPhase1FailureInjector
    {
        public void OnAcquired(Phase1AcquisitionCheckpoint checkpoint)
        {
            onAcquired(checkpoint);
        }
    }
}
