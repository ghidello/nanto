using AwesomeAssertions;

namespace Nanto.Core.Tests;

public sealed class FailureContractTests
{
    [Fact]
    public void HostExceptionCopiesCleanupFailures()
    {
        var failures = new List<Exception> { new InvalidOperationException("cleanup") };
        var exception = new NantoHostException("Host failed.", new InvalidOperationException("primary"))
        {
            Stage = NantoFailureStage.Startup,
            Operation = "CreateHost",
            CleanupExceptions = failures,
        };

        failures.Clear();

        exception.CleanupExceptions.Should().ContainSingle();
        exception.InnerException.Should().NotBeNull();
    }

    [Fact]
    public void EventArgumentsRejectNonUtcTimestamp()
    {
        var action = () => new RendererFailedEventArgs
        {
            Kind = RendererFailureKind.Exited,
            Description = "Renderer exited.",
            WillAttemptRecovery = true,
            OccurredAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.FromHours(2)),
        };

        action.Should().Throw<ArgumentException>();
    }
}