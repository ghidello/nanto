using AwesomeAssertions;

using Nanto.Cli.TestProtocol;

namespace Nanto.Cli.Tests;

public sealed class Phase3LifecycleProtocolTests
{
    [Fact]
    public void SerializeAndParsePreserveStableMarker()
    {
        var value = new Phase3LifecycleEvent
        {
            Marker = Phase3LifecycleMarker.HostRestarted,
            Resource = "host",
            Sequence = 3,
            Reason = "managed-watch-restart",
        };

        string line = Phase3LifecycleProtocol.Serialize(value);
        Phase3LifecycleEvent parsed = Phase3LifecycleProtocol.Parse(line);

        line.Should().StartWith(Phase3LifecycleProtocol.LinePrefix);
        line.Should().Contain("\"marker\":\"HostRestarted\"");
        parsed.Should().BeEquivalentTo(value);
    }

    [Theory]
    [InlineData("frontend ready")]
    [InlineData("absolute\\path")]
    [InlineData("")]
    public void ValidateRejectsNonSymbolicResource(string resource)
    {
        var value = new Phase3LifecycleEvent
        {
            Marker = Phase3LifecycleMarker.FrontendReady,
            Resource = resource,
            Sequence = 0,
        };

        var action = () => Phase3LifecycleProtocol.Validate(value);

        action.Should().Throw<ArgumentException>();
    }
}
