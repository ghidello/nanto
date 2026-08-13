using AwesomeAssertions;

using Microsoft.Extensions.Logging;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowsApplicationHostTests
{
    public static TheoryData<int> HostAcquisitionCheckpoints => new()
    {
        (int)Phase1AcquisitionCheckpoint.ApplicationHostStarted,
        (int)Phase1AcquisitionCheckpoint.UiThreadStarted,
        (int)Phase1AcquisitionCheckpoint.NativeMessageQueueCreated,
        (int)Phase1AcquisitionCheckpoint.DispatcherCreated,
        (int)Phase1AcquisitionCheckpoint.WindowClassRegistered,
        (int)Phase1AcquisitionCheckpoint.WindowCreated,
    };

    [Fact]
    public async Task RunAndStopPublishThePrimaryWindowAndCompleteLifecycle()
    {
        await using var host = CreateHost();
        var states = new List<ApplicationState>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += (_, eventArgs) =>
        {
            states.Add(eventArgs.NewState);
            if (eventArgs.NewState == ApplicationState.Activated)
            {
                activated.TrySetResult();
            }
        };

        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.Task.WaitAsync(TestContext.Current.CancellationToken);

        host.PrimaryWindow.Should().BeOfType<WindowsWindow>().Which.State.Should().Be(WindowState.Running);
        host.Dispatcher.CheckAccess().Should().BeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
        await run;

        host.State.Should().Be(ApplicationState.Closed);
        host.PrimaryWindow.Should().BeNull();
        states.Should().Equal(
            ApplicationState.Creating,
            ApplicationState.Created,
            ApplicationState.Activated,
            ApplicationState.Closing,
            ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        var invokeAfterShutdown = () => host.Dispatcher.InvokeAsync(static () => { });
        invokeAfterShutdown.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task SharedWebViewApplicationIsDisposedOnItsCreatingUiThread()
    {
        var factory = new ThreadRecordingWebViewApplicationFactory();
        await using var host = new WindowsApplicationHost(TimeProvider.System, NoOpPhase1FailureInjector.Instance, factory);
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        await run;

        factory.Application.Should().NotBeNull();
        factory.Application!.DisposedThreadId.Should().Be(factory.Application.CreatedThreadId);
        factory.Application.DisposedSynchronizationContext.Should().BeSameAs(factory.Application.CreatedSynchronizationContext);
    }

    [Fact]
    public async Task PreferredColorSchemePublishesOnlyAfterAUiThreadMutationSucceeds()
    {
        await using var host = CreateHost();
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions() with { PreferredColorScheme = ColorSchemePreference.Dark }, TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        host.PreferredColorScheme.Should().Be(ColorSchemePreference.Dark);
        await host.SetPreferredColorSchemeAsync(ColorSchemePreference.Dark, TestContext.Current.CancellationToken);
        host.PreferredColorScheme.Should().Be(ColorSchemePreference.Dark);
        await host.SetPreferredColorSchemeAsync(ColorSchemePreference.Light, TestContext.Current.CancellationToken);
        host.PreferredColorScheme.Should().Be(ColorSchemePreference.Light);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledChange = () => host.SetPreferredColorSchemeAsync(ColorSchemePreference.System, cancellation.Token).AsTask();
        await canceledChange.Should().ThrowAsync<OperationCanceledException>();
        host.PreferredColorScheme.Should().Be(ColorSchemePreference.Light);

        await host.StopAsync(TestContext.Current.CancellationToken);
        await run;
    }

    [Fact]
    public async Task PreferredColorSchemeCannotChangeAfterStopIsRequested()
    {
        await using var host = CreateHost();
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);
        using var releaseDispatcher = new ManualResetEventSlim();
        var dispatcherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingDispatch = host.Dispatcher.InvokeAsync(
            () =>
            {
                dispatcherEntered.TrySetResult();
                releaseDispatcher.Wait(TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);
        await dispatcherEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var queuedChange = host.SetPreferredColorSchemeAsync(ColorSchemePreference.Light, TestContext.Current.CancellationToken);
        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        var afterStopRequest = () => host.SetPreferredColorSchemeAsync(ColorSchemePreference.Dark, TestContext.Current.CancellationToken).AsTask();

        await afterStopRequest.Should().ThrowAsync<ObjectDisposedException>();
        try
        {
            releaseDispatcher.Set();
            await blockingDispatch;
            await queuedChange.AsTask().Invoking(static task => task).Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            releaseDispatcher.Set();
        }

        host.PreferredColorScheme.Should().Be(ColorSchemePreference.System);
        await stop;
        await run;
    }

    [Fact]
    public async Task NativePrimaryWindowCloseRequestsDefaultApplicationShutdown()
    {
        await using var host = CreateHost();
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);
        var window = host.PrimaryWindow.Should().BeOfType<WindowsWindow>().Subject;

        ((bool)PInvoke.PostMessage(window.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
        await run.WaitAsync(TestContext.Current.CancellationToken);

        host.State.Should().Be(ApplicationState.Closed);
        host.PrimaryWindow.Should().BeNull();
        host.ResourceSnapshot.TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task ExplicitShutdownKeepsRunningAfterThePrimaryWindowCloses()
    {
        await using var host = CreateHost();
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(ShutdownMode.Explicit), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);
        var window = host.PrimaryWindow.Should().BeOfType<WindowsWindow>().Subject;
        var windowClosed = WaitForStateAsync(window, WindowState.Closed);

        ((bool)PInvoke.PostMessage(window.Handle, PInvoke.WM_CLOSE, default, default)).Should().BeTrue();
        await windowClosed.WaitAsync(TestContext.Current.CancellationToken);

        host.State.Should().Be(ApplicationState.Activated);
        host.PrimaryWindow.Should().BeNull();
        run.IsCompleted.Should().BeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
        await run;
        host.ResourceSnapshot.TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task PreCanceledRunStartsAndStopsWithoutCreatingAWindow()
    {
        await using var host = CreateHost();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var states = new List<ApplicationState>();
        host.StateChanged += (_, eventArgs) => states.Add(eventArgs.NewState);

        await host.RunAsync(CreateOptions(), cancellation.Token).WaitAsync(TestContext.Current.CancellationToken);

        states.Should().Equal(ApplicationState.Creating, ApplicationState.Closing, ApplicationState.Closed);
        host.PrimaryWindow.Should().BeNull();
        host.ResourceSnapshot.TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task PreCanceledStopWaitStillRequestsShutdown()
    {
        await using var host = CreateHost();
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stop = host.StopAsync(cancellation.Token);
        await stop.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await run.WaitAsync(TestContext.Current.CancellationToken);

        host.State.Should().Be(ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task ThrowingApplicationLifetimeCancellationCallbackDoesNotPreventShutdown()
    {
        var callbackFailure = new InvalidOperationException("application cancellation callback failed");
        using var factory = new ThrowingLifetimeCancellationWebViewApplicationFactory(callbackFailure);
        var host = new WindowsApplicationHost(TimeProvider.System, NoOpPhase1FailureInjector.Instance, factory);
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        var stop = host.StopAsync(TestContext.Current.CancellationToken).AsTask();
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Teardown);
        exception.Operation.Should().Be("application.cancel-lifetime");
        exception.InnerException.Should().BeOfType<AggregateException>()
            .Which.Flatten().InnerExceptions.Should().ContainSingle()
            .Which.Should().BeSameAs(callbackFailure);
        await stop.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()
            .Where(candidate => ReferenceEquals(candidate, exception));
        host.State.Should().Be(ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task CancellationCallbackFailureIsRetainedAfterAnExistingStartupFailure()
    {
        var startupFailure = new InvalidOperationException("WebView startup failed");
        var callbackFailure = new InvalidOperationException("application cancellation callback failed");
        using var factory = new FailingStartupWithThrowingCancellationCallbackFactory(startupFailure, callbackFailure);
        var host = new WindowsApplicationHost(TimeProvider.System, NoOpPhase1FailureInjector.Instance, factory);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await factory.CreateEntered.WaitAsync(TestContext.Current.CancellationToken);

        var stop = host.StopAsync(TestContext.Current.CancellationToken).AsTask();
        factory.Release();
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Startup);
        exception.Operation.Should().Be("window.create");
        exception.InnerException.Should().BeSameAs(startupFailure);
        exception.CleanupExceptions.Should().ContainSingle().Which.Should().BeSameAs(callbackFailure);
        await stop.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()
            .Where(candidate => ReferenceEquals(candidate, exception));
        host.State.Should().Be(ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task LoggerCreationFailureDoesNotClaimOrStartTheHost()
    {
        await using var host = CreateHost();
        var loggerFailure = new InvalidOperationException("logger creation failed");
        var failingOptions = CreateOptions() with { LoggerFactory = new ThrowingLoggerFactory(loggerFailure) };

        Action failedRun = () => _ = host.RunAsync(failingOptions, TestContext.Current.CancellationToken);

        failedRun.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(loggerFailure);
        host.State.Should().Be(ApplicationState.NotStarted);
        host.ResourceSnapshot.TotalActive.Should().Be(0);

        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await run;
    }

    [Fact]
    public async Task ShutdownTimeoutFailsTheRunWhileTeardownContinuesSafely()
    {
        var host = CreateHost();
        using var releaseClosingHandler = new ManualResetEventSlim();
        var closingHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        host.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewState == ApplicationState.Closing)
            {
                closingHandlerEntered.TrySetResult();
                releaseClosingHandler.Wait();
            }
        };
        var options = CreateOptions() with { ShutdownTimeout = TimeSpan.FromMilliseconds(100) };
        var run = host.RunAsync(options, TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        var stop = host.StopAsync(TestContext.Current.CancellationToken).AsTask();
        await closingHandlerEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        NantoHostException exception;
        try
        {
            exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;
        }
        finally
        {
            releaseClosingHandler.Set();
        }

        exception.Stage.Should().Be(NantoFailureStage.Teardown);
        exception.Operation.Should().Be("application.shutdown-timeout");
        exception.InnerException.Should().BeOfType<TimeoutException>();
        await stop.Invoking(static task => task).Should().ThrowAsync<NantoHostException>();
        await host.TeardownCompletion.WaitAsync(TestContext.Current.CancellationToken);
        host.State.Should().Be(ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task ShutdownTimeoutStartsWhileNativeStartupIsStillPending()
    {
        var factory = new BlockingWebViewApplicationFactory();
        var host = new WindowsApplicationHost(TimeProvider.System, NoOpPhase1FailureInjector.Instance, factory);
        var options = CreateOptions() with { ShutdownTimeout = TimeSpan.FromMilliseconds(100) };
        var run = host.RunAsync(options, TestContext.Current.CancellationToken);
        await factory.CreateEntered.WaitAsync(TestContext.Current.CancellationToken);

        var stop = host.StopAsync(TestContext.Current.CancellationToken).AsTask();
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Teardown);
        exception.Operation.Should().Be("application.shutdown-timeout");
        exception.InnerException.Should().BeOfType<TimeoutException>();
        await stop.Invoking(static task => task).Should().ThrowAsync<NantoHostException>();
        host.TeardownCompletion.IsCompleted.Should().BeFalse();

        factory.Release();
        await host.TeardownCompletion.WaitAsync(TestContext.Current.CancellationToken);
        host.State.Should().Be(ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task TeardownFailureAfterShutdownTimeoutIsLogged()
    {
        var cleanupFailure = new InvalidOperationException("late WebView cleanup failed");
        var loggerFactory = new RecordingLoggerFactory();
        var host = new WindowsApplicationHost(
            TimeProvider.System,
            NoOpPhase1FailureInjector.Instance,
            new FailingWindowCleanupWebViewApplicationFactory(cleanupFailure));
        using var releaseClosingHandler = new ManualResetEventSlim();
        var closingHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        host.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewState == ApplicationState.Closing)
            {
                closingHandlerEntered.TrySetResult();
                releaseClosingHandler.Wait();
            }
        };
        var options = CreateOptions() with
        {
            LoggerFactory = loggerFactory,
            ShutdownTimeout = TimeSpan.FromMilliseconds(100),
        };
        var run = host.RunAsync(options, TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        var stop = host.StopAsync(TestContext.Current.CancellationToken).AsTask();
        await closingHandlerEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>();
        }
        finally
        {
            releaseClosingHandler.Set();
        }

        await stop.Invoking(static task => task).Should().ThrowAsync<NantoHostException>();
        await host.TeardownCompletion.WaitAsync(TestContext.Current.CancellationToken);
        await loggerFactory.ExceptionLogged.WaitAsync(TestContext.Current.CancellationToken);
        loggerFactory.Exceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be(cleanupFailure.Message);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task NativePrimaryWindowCloseStartsTheShutdownDeadlineBeforeWebViewCleanupCompletes()
    {
        using var factory = new BlockingWindowCleanupWebViewApplicationFactory();
        var host = new WindowsApplicationHost(TimeProvider.System, NoOpPhase1FailureInjector.Instance, factory);
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var options = CreateOptions() with { ShutdownTimeout = TimeSpan.FromMilliseconds(100) };
        var run = host.RunAsync(options, TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        var close = host.PrimaryWindow!.CloseAsync(TestContext.Current.CancellationToken).AsTask();
        await factory.CleanupEntered.WaitAsync(TestContext.Current.CancellationToken);
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Teardown);
        exception.Operation.Should().Be("application.shutdown-timeout");
        factory.ReleaseCleanup();
        await close;
        await host.TeardownCompletion.WaitAsync(TestContext.Current.CancellationToken);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task NativePrimaryWindowClosePropagatesWebViewCleanupFailureThroughTheHost()
    {
        var cleanupFailure = new InvalidOperationException("native-close WebView cleanup failed");
        var host = new WindowsApplicationHost(
            TimeProvider.System,
            NoOpPhase1FailureInjector.Instance,
            new FailingWindowCleanupWebViewApplicationFactory(cleanupFailure));
        var activated = WaitForStateAsync(host, ApplicationState.Activated);
        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await activated.WaitAsync(TestContext.Current.CancellationToken);

        var close = host.PrimaryWindow!.CloseAsync(TestContext.Current.CancellationToken).AsTask();
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Teardown);
        exception.InnerException.Should().BeSameAs(cleanupFailure);
        await close.Invoking(static task => task).Should().ThrowAsync<InvalidOperationException>()
            .Where(exception => ReferenceEquals(exception, cleanupFailure));
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Theory]
    [MemberData(nameof(HostAcquisitionCheckpoints))]
    public async Task AcquisitionCheckpointFailureReleasesEveryHostResource(int failingCheckpointValue)
    {
        var failingCheckpoint = (Phase1AcquisitionCheckpoint)failingCheckpointValue;
        var failure = new InvalidOperationException($"{failingCheckpoint} failed");
        var reachedCheckpoints = new List<Phase1AcquisitionCheckpoint>();
        var host = new WindowsApplicationHost(
            TimeProvider.System,
            new DelegatePhase1FailureInjector(checkpoint =>
            {
                reachedCheckpoints.Add(checkpoint);
                if (checkpoint == failingCheckpoint)
                {
                    throw failure;
                }
            }),
            NoOpWindowsWebViewApplicationFactory.Instance);

        var run = host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Startup);
        exception.Operation.Should().Be(failingCheckpoint is Phase1AcquisitionCheckpoint.WindowClassRegistered or Phase1AcquisitionCheckpoint.WindowCreated
            ? "window.create"
            : "application.start");
        exception.InnerException.Should().BeSameAs(failure);
        exception.CleanupExceptions.Should().BeEmpty();
        reachedCheckpoints.Should().EndWith(failingCheckpoint);
        host.State.Should().Be(ApplicationState.Closed);
        host.ResourceSnapshot.TotalActive.Should().Be(0);
        await host.Invoking(static value => value.DisposeAsync().AsTask()).Should().ThrowAsync<NantoHostException>();
    }

    [Fact]
    public async Task DisposeBeforeRunLeavesTheHostUnstarted()
    {
        var host = new WindowsApplicationHost();

        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.DisposeAsync();

        host.State.Should().Be(ApplicationState.NotStarted);
        host.Invoking(static value => value.Dispatcher).Should().Throw<InvalidOperationException>();
        var run = () => host.RunAsync(CreateOptions(), TestContext.Current.CancellationToken);
        await run.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static NantoApplicationOptions CreateOptions(ShutdownMode shutdownMode = ShutdownMode.OnPrimaryWindowClosed) => new()
    {
        ApplicationId = "com.example.nanto-windows-tests",
        Assets = new UnusedAssetProvider(),
        PrimaryWindow = new WindowOptions
        {
            Title = "Nanto hidden application host window",
            StartVisible = false,
        },
        ShutdownMode = shutdownMode,
    };

    private static WindowsApplicationHost CreateHost() => new(
        TimeProvider.System,
        NoOpPhase1FailureInjector.Instance,
        NoOpWindowsWebViewApplicationFactory.Instance);

    private static Task WaitForStateAsync(WindowsApplicationHost host, ApplicationState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewState == state)
            {
                reached.TrySetResult();
            }
        };
        return reached.Task;
    }

    private static Task WaitForStateAsync(WindowsWindow window, WindowState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewState == state)
            {
                reached.TrySetResult();
            }
        };
        return reached.Task;
    }

    private sealed class UnusedAssetProvider : IWebAssetProvider
    {
        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Win32-only host must not prepare WebView assets.");
    }

    private sealed class ThrowingLoggerFactory(Exception failure) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
        }

        public ILogger CreateLogger(string categoryName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
            throw failure;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingLifetimeCancellationWebViewApplicationFactory(Exception failure) : IWindowsWebViewApplicationFactory, IDisposable
    {
        private CancellationTokenRegistration _registration;

        public ValueTask<IWindowsWebViewApplication> CreateAsync(
            Nanto.Hosting.ValidatedApplicationOptions options,
            IUiDispatcher dispatcher,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector,
            CancellationToken cancellationToken)
        {
            _registration = cancellationToken.UnsafeRegister(static state => throw (Exception)state!, failure);
            return ValueTask.FromResult<IWindowsWebViewApplication>(new ThreadRecordingWebViewApplication());
        }

        public void Dispose() => _registration.Dispose();
    }

    private sealed class FailingStartupWithThrowingCancellationCallbackFactory(
        Exception startupFailure,
        Exception callbackFailure) : IWindowsWebViewApplicationFactory, IDisposable
    {
        private readonly TaskCompletionSource _createEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;

        public Task CreateEntered => _createEntered.Task;

        public async ValueTask<IWindowsWebViewApplication> CreateAsync(
            Nanto.Hosting.ValidatedApplicationOptions options,
            IUiDispatcher dispatcher,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector,
            CancellationToken cancellationToken)
        {
            _registration = cancellationToken.UnsafeRegister(static state => throw (Exception)state!, callbackFailure);
            _createEntered.TrySetResult();
            await _release.Task;
            throw startupFailure;
        }

        public void Dispose() => _registration.Dispose();

        public void Release() => _release.TrySetResult();
    }

    private sealed class ThreadRecordingWebViewApplicationFactory : IWindowsWebViewApplicationFactory
    {
        public ThreadRecordingWebViewApplication? Application { get; private set; }

        public ValueTask<IWindowsWebViewApplication> CreateAsync(
            Nanto.Hosting.ValidatedApplicationOptions options,
            IUiDispatcher dispatcher,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Application = new ThreadRecordingWebViewApplication();
            return ValueTask.FromResult<IWindowsWebViewApplication>(Application);
        }
    }

    private sealed class BlockingWebViewApplicationFactory : IWindowsWebViewApplicationFactory
    {
        private readonly TaskCompletionSource _createEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CreateEntered => _createEntered.Task;

        public async ValueTask<IWindowsWebViewApplication> CreateAsync(
            Nanto.Hosting.ValidatedApplicationOptions options,
            IUiDispatcher dispatcher,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector,
            CancellationToken cancellationToken)
        {
            _createEntered.TrySetResult();
            await _release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The blocking factory was released without cancellation.");
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class BlockingWindowCleanupWebViewApplicationFactory : IWindowsWebViewApplicationFactory, IDisposable
    {
        private readonly BlockingWindowCleanupWebViewApplication _application = new();

        public Task CleanupEntered => _application.CleanupEntered;

        public ValueTask<IWindowsWebViewApplication> CreateAsync(
            Nanto.Hosting.ValidatedApplicationOptions options,
            IUiDispatcher dispatcher,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IWindowsWebViewApplication>(_application);

        public void ReleaseCleanup() => _application.ReleaseCleanup();

        public void Dispose() => ReleaseCleanup();
    }

    private sealed class BlockingWindowCleanupWebViewApplication : IWindowsWebViewApplication
    {
        private readonly BlockingCleanupWebViewWindow _window = new();

        public Task CleanupEntered => _window.CleanupEntered;

        public ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            HWND parentWindow,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IWindowsWebViewWindow>(_window);

        public ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WaitForReadinessAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<string>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void ReleaseCleanup() => _window.ReleaseCleanup();
    }

    private sealed class BlockingCleanupWebViewWindow : IWindowsWebViewWindow
    {
        private readonly TaskCompletionSource _cleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CleanupEntered => _cleanupEntered.Task;

        public Task Readiness => Task.CompletedTask;

        public void SetBounds(int width, int height)
        {
        }

        public async ValueTask DisposeAsync()
        {
            _cleanupEntered.TrySetResult();
            await _releaseCleanup.Task;
        }

        public void ReleaseCleanup() => _releaseCleanup.TrySetResult();
    }

    private sealed class FailingWindowCleanupWebViewApplicationFactory(Exception cleanupFailure) : IWindowsWebViewApplicationFactory
    {
        public ValueTask<IWindowsWebViewApplication> CreateAsync(
            Nanto.Hosting.ValidatedApplicationOptions options,
            IUiDispatcher dispatcher,
            ResourceLedger resourceLedger,
            IPhase1FailureInjector failureInjector,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IWindowsWebViewApplication>(new FailingWindowCleanupWebViewApplication(cleanupFailure));
    }

    private sealed class FailingWindowCleanupWebViewApplication(Exception cleanupFailure) : IWindowsWebViewApplication
    {
        public ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            HWND parentWindow,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IWindowsWebViewWindow>(new FailingCleanupWebViewWindow(cleanupFailure));

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

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly TaskCompletionSource _exceptionLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Exception> Exceptions { get; } = [];

        public Task ExceptionLogged => _exceptionLogged.Task;

        public void AddProvider(ILoggerProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
        }

        public ILogger CreateLogger(string categoryName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
            return new RecordingLogger(Exceptions, _exceptionLogged);
        }

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<Exception> exceptions, TaskCompletionSource exceptionLogged) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception is not null)
                {
                    exceptions.Add(exception);
                    exceptionLogged.TrySetResult();
                }
            }
        }
    }

    private sealed class ThreadRecordingWebViewApplication : IWindowsWebViewApplication
    {
        public int CreatedThreadId { get; } = Environment.CurrentManagedThreadId;

        public SynchronizationContext? CreatedSynchronizationContext { get; } = SynchronizationContext.Current;

        public int? DisposedThreadId { get; private set; }

        public SynchronizationContext? DisposedSynchronizationContext { get; private set; }

        public ValueTask<IWindowsWebViewWindow> CreateWindowAsync(
            HWND parentWindow,
            WindowOptions options,
            ColorSchemePreference preferredColorScheme,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IWindowsWebViewWindow>(ThreadRecordingWebViewWindow.Instance);
        }

        public ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WaitForReadinessAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<string> WaitForDiagnosticMessageAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<string>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(canceled: true));

        public ValueTask DisposeAsync()
        {
            DisposedThreadId = Environment.CurrentManagedThreadId;
            DisposedSynchronizationContext = SynchronizationContext.Current;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThreadRecordingWebViewWindow : IWindowsWebViewWindow
    {
        public static ThreadRecordingWebViewWindow Instance { get; } = new();

        public Task Readiness => Task.CompletedTask;

        public void SetBounds(int width, int height)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}