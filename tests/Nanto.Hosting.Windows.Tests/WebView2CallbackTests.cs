using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WebView2CallbackTests
{
    [Fact]
    public async Task EnvironmentCallbackLetsCancellationWinAtNativeCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new EnvironmentCreatedHandler(cancellation.Token);
        cancellation.Cancel();

        handler.Completion.IsCompleted.Should().BeFalse();
        handler.Invoke(unchecked((int)0x80004005), 0).Should().Be(0);

        await handler.Completion.Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ControllerCallbackLetsCancellationWinAtNativeCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ControllerCreatedHandler(cancellation.Token);
        cancellation.Cancel();

        handler.Completion.IsCompleted.Should().BeFalse();
        handler.Invoke(unchecked((int)0x80004005), 0).Should().Be(0);

        await handler.Completion.Invoking(static task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SynchronousEnvironmentFailureIsPublishedExactlyOnce()
    {
        var handler = new EnvironmentCreatedHandler(CancellationToken.None);
        var first = new InvalidOperationException("first");

        handler.FailSynchronously(first);
        handler.Invoke(unchecked((int)0x80004005), 0).Should().Be(0);

        var failure = await handler.Completion.Invoking(static task => task).Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Should().BeSameAs(first);
    }
}
