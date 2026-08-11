using AwesomeAssertions;

using Microsoft.Extensions.Logging;

using Windows.Win32;

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
        await using var host = new WindowsApplicationHost();
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
    public async Task NativePrimaryWindowCloseRequestsDefaultApplicationShutdown()
    {
        await using var host = new WindowsApplicationHost();
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
        await using var host = new WindowsApplicationHost();
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
        await using var host = new WindowsApplicationHost();
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
        await using var host = new WindowsApplicationHost();
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
    public async Task LoggerCreationFailureDoesNotClaimOrStartTheHost()
    {
        await using var host = new WindowsApplicationHost();
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
        var host = new WindowsApplicationHost();
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
            }));

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
}
