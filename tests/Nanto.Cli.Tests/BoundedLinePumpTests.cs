using AwesomeAssertions;

using Nanto.Cli.Processes;

namespace Nanto.Cli.Tests;

public sealed class BoundedLinePumpTests
{
    [Fact]
    public async Task RunBoundsLongLinesAndRetainedHistory()
    {
        string input = string.Join('\n', Enumerable.Range(0, 40).Select(index => index == 39 ? new string('x', 5000) : $"line-{index}"));
        using var output = new StringWriter();
        var pump = new BoundedLinePump("frontend", output);

        await pump.RunAsync(new StringReader(input));

        string[] retained = pump.Snapshot();
        retained.Should().HaveCount(32);
        retained[0].Should().Be("line-8");
        retained[^1].Should().EndWith("…[truncated]").And.HaveLength(4096 + "…[truncated]".Length);
        output.ToString().Should().Contain("[frontend] line-0");
    }
}
