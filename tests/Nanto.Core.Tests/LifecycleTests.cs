using AwesomeAssertions;

namespace Nanto.Core.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public void ApplicationLifecyclePublishesValidTransitionsAndUtcTime()
    {
        var time = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var lifecycle = new ApplicationLifecycle(new FixedTimeProvider(time));

        var transition = lifecycle.TransitionTo(ApplicationState.Creating);

        lifecycle.State.Should().Be(ApplicationState.Creating);
        transition.OldState.Should().Be(ApplicationState.NotStarted);
        transition.NewState.Should().Be(ApplicationState.Creating);
        transition.OccurredAt.Should().Be(time);
    }

    [Fact]
    public void ApplicationLifecycleRejectsInvalidTransition()
    {
        var lifecycle = new ApplicationLifecycle(TimeProvider.System);

        var action = () => lifecycle.TransitionTo(ApplicationState.Activated);

        action.Should().Throw<InvalidOperationException>();
        lifecycle.State.Should().Be(ApplicationState.NotStarted);
    }

    [Fact]
    public void WindowLifecycleSupportsFailureCleanupPath()
    {
        var lifecycle = new WindowLifecycle(TimeProvider.System);

        lifecycle.TransitionTo(WindowState.Initializing);
        lifecycle.TransitionTo(WindowState.Failed, new InvalidOperationException("failure"));
        lifecycle.TransitionTo(WindowState.Closing);
        lifecycle.TransitionTo(WindowState.Closed);

        lifecycle.State.Should().Be(WindowState.Closed);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}