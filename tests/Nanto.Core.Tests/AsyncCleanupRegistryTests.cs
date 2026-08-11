using AwesomeAssertions;

using Nanto.Hosting;

namespace Nanto.Core.Tests;

public sealed class AsyncCleanupRegistryTests
{
    [Fact]
    public async Task DrainRunsEveryCleanupInReverseOrderAndAggregatesFailures()
    {
        var cleanup = new AsyncCleanupRegistry();
        var operations = new List<string>();
        cleanup.Push("first", () => RecordAsync("first"));
        cleanup.Push("second", () => throw new InvalidOperationException("second failed"));
        cleanup.Push("third", () => RecordAsync("third"));

        var failures = await cleanup.DrainAsync();

        operations.Should().Equal("third", "first");
        failures.Should().ContainSingle().Which.Message.Should().Contain("second");
        cleanup.Count.Should().Be(0);

        ValueTask RecordAsync(string operation)
        {
            operations.Add(operation);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RepeatedDrainIsAnEmptyNoOp()
    {
        var cleanup = new AsyncCleanupRegistry();
        cleanup.Push("cleanup", static () => ValueTask.CompletedTask);

        await cleanup.DrainAsync();
        var failures = await cleanup.DrainAsync();

        failures.Should().BeEmpty();
    }
}
