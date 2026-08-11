using AwesomeAssertions;

using Microsoft.Extensions.Logging;

namespace Nanto.Testing.Tests;

public sealed class FakeNantoApplicationHostTests
{
    [Fact]
    public async Task RunAndStopPublishTheCompleteApplicationLifecycle()
    {
        using var dispatcher = new ManualUiDispatcher();
        var timeProvider = new TestingFixture.FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var host = new FakeNantoApplicationHost(dispatcher, timeProvider);
        var recorder = new LifecycleRecorder(timeProvider);
        using var subscription = recorder.Attach(host);

        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);

        host.PrimaryWindow.Should().NotBeNull();
        host.Dispatcher.Should().BeSameAs(dispatcher);

        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await stop;
        await run;

        host.State.Should().Be(ApplicationState.Closed);
        host.PrimaryWindow.Should().BeNull();
        recorder.Snapshot()
            .Select(static record => ((ApplicationStateChangedEventArgs)record.EventArgs).NewState)
            .Should().Equal(
                ApplicationState.Creating,
                ApplicationState.Created,
                ApplicationState.Activated,
                ApplicationState.Closing,
                ApplicationState.Closed);
    }

    [Fact]
    public async Task LifecycleGatesPauseAtEveryOrderlyCheckpoint()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        host.CreationGate.Close();
        host.ActivationGate.Close();
        host.StopGate.Close();
        host.CloseGate.Close();

        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Creating);
        await host.CreationGate.WaitUntilReachedAsync(TestContext.Current.CancellationToken);
        host.PrimaryWindow.Should().BeNull();

        host.CreationGate.Open();
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Created);
        await host.ActivationGate.WaitUntilReachedAsync(TestContext.Current.CancellationToken);

        host.ActivationGate.Open();
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);
        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        await host.StopGate.WaitUntilReachedAsync(TestContext.Current.CancellationToken);
        host.State.Should().Be(ApplicationState.Activated);

        host.StopGate.Open();
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Closing);
        await host.CloseGate.WaitUntilReachedAsync(TestContext.Current.CancellationToken);
        host.PrimaryWindow.Should().NotBeNull();

        host.CloseGate.Open();
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await stop;
        await run;
    }

    [Fact]
    public async Task PreCanceledRunStillCreatesAndClosesTheApplicationLifecycle()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var states = new List<ApplicationState>();
        host.StateChanged += (_, eventArgs) => states.Add(eventArgs.NewState);
        var run = host.RunAsync(TestingFixture.CreateOptions(), cancellation.Token);
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await run;

        states.Should().Equal(ApplicationState.Creating, ApplicationState.Closing, ApplicationState.Closed);
        host.PrimaryWindow.Should().BeNull();
    }

    [Fact]
    public async Task PreCanceledStopWaitStillRequestsShutdown()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stop = host.StopAsync(cancellation.Token);
        await stop.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await run;

        host.State.Should().Be(ApplicationState.Closed);
    }

    [Fact]
    public async Task StartupAndCleanupFailuresArePreserved()
    {
        using var dispatcher = new ManualUiDispatcher();
        var startupFailure = new InvalidOperationException("initialization failed");
        var cleanupFailure = new InvalidOperationException("close failed");
        var failurePlan = new FailurePlan(
        [
            new FailurePlanStep { Operation = "application.create" },
            new FailurePlanStep { Operation = "window.initialize", Failure = startupFailure },
            new FailurePlanStep { Operation = "window.close", Failure = cleanupFailure },
        ]);
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System, failurePlan);
        host.FailureGate.Close();

        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Failed);
        await host.FailureGate.WaitUntilReachedAsync(TestContext.Current.CancellationToken);
        run.IsCompleted.Should().BeFalse();

        host.FailureGate.Open();
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.Stage.Should().Be(NantoFailureStage.Startup);
        exception.Operation.Should().Be("window.initialize");
        exception.InnerException.Should().BeSameAs(startupFailure);
        exception.CleanupExceptions.Should().ContainSingle().Which.InnerException.Should().BeSameAs(cleanupFailure);
        host.State.Should().Be(ApplicationState.Closed);
        failurePlan.VerifyComplete();
    }

    [Fact]
    public async Task WindowCloseFailureIsNotRepeatedAsACleanupFailure()
    {
        using var dispatcher = new ManualUiDispatcher();
        var closeFailure = new InvalidOperationException("close failed");
        var failurePlan = new FailurePlan(
        [
            new FailurePlanStep { Operation = "application.create" },
            new FailurePlanStep { Operation = "window.initialize" },
            new FailurePlanStep { Operation = "application.activate" },
            new FailurePlanStep { Operation = "window.close", Failure = closeFailure },
        ]);
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System, failurePlan);
        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);
        var window = host.PrimaryWindow.Should().BeOfType<FakeNantoWindow>().Subject;

        var close = window.CloseAsync(TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await close.AsTask().Invoking(static task => task).Should().ThrowAsync<InvalidOperationException>();
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        var exception = (await run.Invoking(static task => task).Should().ThrowAsync<NantoHostException>()).Which;

        exception.InnerException.Should().BeSameAs(closeFailure);
        exception.CleanupExceptions.Should().BeEmpty();
        host.State.Should().Be(ApplicationState.Closed);
        failurePlan.VerifyComplete();
    }

    [Fact]
    public async Task StateHandlerFailureIsLoggedWithoutSuppressingLaterHandlers()
    {
        using var dispatcher = new ManualUiDispatcher();
        using var loggerFactory = new TestingFixture.RecordingLoggerFactory();
        var handlerFailure = new InvalidOperationException("handler failed");
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        var laterHandlerCalls = 0;
        host.StateChanged += (_, _) => throw handlerFailure;
        host.StateChanged += (_, _) => laterHandlerCalls++;
        var options = TestingFixture.CreateOptions() with { LoggerFactory = loggerFactory };

        var run = host.RunAsync(options, TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);

        laterHandlerCalls.Should().Be(3);
        loggerFactory.Exceptions.Should().HaveCount(3).And.OnlyContain(exception => ReferenceEquals(exception, handlerFailure));

        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await stop;
        await run;
    }

    [Fact]
    public async Task ExplicitShutdownKeepsRunningAfterThePrimaryWindowCloses()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        var run = host.RunAsync(TestingFixture.CreateOptions(ShutdownMode.Explicit), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);
        var window = host.PrimaryWindow.Should().BeOfType<FakeNantoWindow>().Subject;

        var close = window.CloseAsync(TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await close;

        host.State.Should().Be(ApplicationState.Activated);
        host.PrimaryWindow.Should().BeNull();
        run.IsCompleted.Should().BeFalse();

        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await stop;
        await run;
    }

    [Fact]
    public async Task SecondRunIsRejectedWhileRunningAndAfterClosure()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);

        var secondRun = () => host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await secondRun.Should().ThrowAsync<InvalidOperationException>();

        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await stop;
        await run;

        var runAfterClosure = () => host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await runAfterClosure.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisposeBeforeRunDoesNotStartIt()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);

        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.DisposeAsync();

        host.State.Should().Be(ApplicationState.NotStarted);
        var readDispatcher = () => host.Dispatcher;
        readDispatcher.Should().Throw<InvalidOperationException>();
        var run = () => host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await run.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task LoggerCreationFailureDoesNotClaimTheFakeHost()
    {
        using var dispatcher = new ManualUiDispatcher();
        var host = new FakeNantoApplicationHost(dispatcher, TimeProvider.System);
        var loggerFailure = new InvalidOperationException("logger creation failed");
        var failingOptions = TestingFixture.CreateOptions() with { LoggerFactory = new ThrowingLoggerFactory(loggerFailure) };
        Action failedRun = () => _ = host.RunAsync(failingOptions, TestContext.Current.CancellationToken);

        failedRun.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(loggerFailure);
        host.State.Should().Be(ApplicationState.NotStarted);

        var run = host.RunAsync(TestingFixture.CreateOptions(), TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => host.State == ApplicationState.Activated);
        var stop = host.StopAsync(TestContext.Current.CancellationToken);
        await TestingFixture.DriveUntilAsync(dispatcher, () => run.IsCompleted);
        await stop;
        await run;
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
