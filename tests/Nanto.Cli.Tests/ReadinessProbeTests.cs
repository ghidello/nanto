using System.Net;

using AwesomeAssertions;

using Nanto.Cli.Processes;

namespace Nanto.Cli.Tests;

public sealed class ReadinessProbeTests
{
    [Fact]
    public async Task AnyHttpResponseProvesReachability()
    {
        var probe = new ReadinessProbe(new DelegateHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

        bool reachable = await probe.IsReachableAsync(new Uri("http://localhost:5173/"), CancellationToken.None);

        reachable.Should().BeTrue();
    }

    [Fact]
    public async Task WaitRetriesUntilReachable()
    {
        int attempts = 0;
        var probe = new ReadinessProbe(new DelegateHandler((_, _) =>
        {
            attempts++;
            return attempts < 3
                ? throw new HttpRequestException("not ready")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        await probe.WaitAsync(new Uri("http://localhost:5173/"), TimeSpan.FromSeconds(2), static () => false, CancellationToken.None);

        attempts.Should().Be(3);
    }

    [Fact]
    public async Task WaitFailsWhenOwnedProcessExits()
    {
        var probe = new ReadinessProbe(new DelegateHandler(static (_, _) => throw new HttpRequestException("not ready")));

        var action = () => probe.WaitAsync(new Uri("http://localhost:5173/"), TimeSpan.FromSeconds(2), static () => true, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exited*");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }
}
