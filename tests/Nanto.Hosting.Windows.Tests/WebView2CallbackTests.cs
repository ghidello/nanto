using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WebView2CallbackTests
{
    private static readonly IReadOnlySet<string> _assets = new HashSet<string>(["/index.html"], StringComparer.Ordinal);

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

    [Fact]
    public void NavigationStartingCancelsBeforeReadingAndResetsOnlyForAllowedUri()
    {
        var calls = new List<string>();

        var result = NavigationStartingHandler.Evaluate(
            _assets,
            () =>
            {
                calls.Add("read");
                return (0, "https://app.nanto.invalid/index.html");
            },
            cancel =>
            {
                calls.Add($"cancel:{cancel}");
                return 0;
            });

        result.Should().Be(0);
        calls.Should().Equal("cancel:1", "read", "cancel:0");
    }

    [Theory]
    [InlineData("https://app.nanto.invalid/missing.html")]
    [InlineData("not a URI")]
    public void NavigationStartingRemainsCancelledForRejectedUri(string uri)
    {
        var cancelValues = new List<int>();

        var result = NavigationStartingHandler.Evaluate(
            _assets,
            () => (0, uri),
            cancel =>
            {
                cancelValues.Add(cancel);
                return 0;
            });

        result.Should().Be(0);
        cancelValues.Should().Equal(1);
    }

    [Fact]
    public void NavigationStartingReturnsCancelFailureWithoutReadingUri()
    {
        var read = false;
        const int failure = unchecked((int)0x80004005);

        var result = NavigationStartingHandler.Evaluate(
            _assets,
            () =>
            {
                read = true;
                return (0, "https://app.nanto.invalid/index.html");
            },
            _ => failure);

        result.Should().Be(failure);
        read.Should().BeFalse();
    }

    [Fact]
    public void NavigationStartingReturnsUriFailureAfterEstablishingCancellation()
    {
        var cancelValues = new List<int>();
        const int failure = unchecked((int)0x80004005);

        var result = NavigationStartingHandler.Evaluate(
            _assets,
            () => (failure, string.Empty),
            cancel =>
            {
                cancelValues.Add(cancel);
                return 0;
            });

        result.Should().Be(failure);
        cancelValues.Should().Equal(1);
    }

    [Fact]
    public void NavigationStartingReturnsFailureForNullEventArguments()
    {
        var handler = new NavigationStartingHandler(_assets);

        handler.Invoke(0, 0).Should().BeNegative();
    }
}