using AwesomeAssertions;

namespace Nanto.Testing.Tests;

public sealed class FakeNantoWindowTests
{
    [Fact]
    public async Task MutationsAreAppliedOnTheDispatcherAndRecorded()
    {
        using var dispatcher = new ManualUiDispatcher();
        var time = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
        var window = new FakeNantoWindow(
            dispatcher,
            new WindowOptions { Title = "Initial" },
            new TestingFixture.FixedTimeProvider(time));
        var bounds = new WindowBounds(20, 30, 640, 480);

        var setTitle = window.SetTitleAsync("Updated", TestContext.Current.CancellationToken);
        var setBounds = window.SetBoundsAsync(bounds, TestContext.Current.CancellationToken);
        var activate = window.ActivateAsync(TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await setTitle;
        await setBounds;
        await activate;

        window.Title.Should().Be("Updated");
        window.Bounds.Should().Be(bounds);
        window.Mutations.Select(static mutation => mutation.Kind).Should().Equal(
            FakeWindowMutationKind.TitleChanged,
            FakeWindowMutationKind.BoundsChanged,
            FakeWindowMutationKind.Activated);
        window.Mutations.Should().OnlyContain(mutation => mutation.OccurredAt == time);
    }

    [Fact]
    public async Task PreCanceledCallerWaitStillRequestsOneClose()
    {
        using var dispatcher = new ManualUiDispatcher();
        var window = new FakeNantoWindow(dispatcher, new WindowOptions { Title = "Window" }, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var firstClose = window.CloseAsync(cancellation.Token);
        var secondClose = window.CloseAsync(CancellationToken.None);

        await firstClose.AsTask().Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
        await dispatcher.DrainAsync();
        await secondClose;

        window.State.Should().Be(WindowState.Closed);
        window.IsVisible.Should().BeFalse();
        window.Mutations.Should().ContainSingle(mutation => mutation.Kind == FakeWindowMutationKind.CloseRequested);
    }

    [Fact]
    public async Task RendererFailureIsRaisedOnTheDispatcher()
    {
        using var dispatcher = new ManualUiDispatcher();
        using var loggerFactory = new TestingFixture.RecordingLoggerFactory();
        var window = new FakeNantoWindow(
            dispatcher,
            new WindowOptions { Title = "Window" },
            TimeProvider.System,
            loggerFactory: loggerFactory);
        var eventArgs = new RendererFailedEventArgs
        {
            Kind = RendererFailureKind.Exited,
            Description = "Renderer exited",
            WillAttemptRecovery = true,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        RendererFailedEventArgs? observed = null;
        var hadDispatcherAccess = false;
        var handlerFailure = new InvalidOperationException("handler failed");
        window.RendererFailed += (_, _) => throw handlerFailure;
        window.RendererFailed += (_, args) =>
        {
            observed = args;
            hadDispatcherAccess = dispatcher.CheckAccess();
        };

        var raise = window.RaiseRendererFailureAsync(eventArgs, TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await raise;

        observed.Should().BeSameAs(eventArgs);
        hadDispatcherAccess.Should().BeTrue();
        loggerFactory.Exceptions.Should().ContainSingle().Which.Should().BeSameAs(handlerFailure);
    }

    [Fact]
    public async Task InlineCloseFailureIsCachedAndNotRetried()
    {
        using var dispatcher = new ManualUiDispatcher();
        var failure = new InvalidOperationException("planned close failure");
        var plan = new FailurePlan([new FailurePlanStep { Operation = "window.close", Failure = failure }]);
        var window = new FakeNantoWindow(dispatcher, new WindowOptions { Title = "Window" }, TimeProvider.System, plan);
        Task? firstClose = null;
        Task? secondClose = null;

        var invocation = dispatcher.InvokeAsync(
            () =>
            {
                firstClose = window.CloseAsync(CancellationToken.None).AsTask();
                secondClose = window.CloseAsync(CancellationToken.None).AsTask();
            },
            TestContext.Current.CancellationToken);
        await dispatcher.DrainAsync();
        await invocation;

        firstClose.Should().BeSameAs(secondClose);
        (await firstClose!.Invoking(static task => task).Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        window.Mutations.Should().ContainSingle(mutation => mutation.Kind == FakeWindowMutationKind.CloseRequested);
        plan.VerifyComplete();
    }
}