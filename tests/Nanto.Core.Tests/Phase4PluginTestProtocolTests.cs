using AwesomeAssertions;

using Nanto.Plugin.TestProtocol;

namespace Nanto.Core.Tests;

public sealed class Phase4PluginTestProtocolTests
{
    [Fact]
    public void SerializeAndParsePreserveLifecycleEvidence()
    {
        var value = new Phase4PluginLifecycleEvent
        {
            Plugin = "fixture.filesystem",
            Scope = Phase4PluginScope.Window,
            Transition = Phase4PluginLifecycleTransition.LeaseRevoked,
            Sequence = 7,
        };

        string line = Phase4PluginTestProtocol.Serialize(value);
        Phase4PluginLifecycleEvent parsed = Phase4PluginTestProtocol.Parse(line);

        line.Should().StartWith(Phase4PluginTestProtocol.LinePrefix);
        line.Should().Contain("\"transition\":\"LeaseRevoked\"");
        parsed.Should().BeEquivalentTo(value);
    }

    [Theory]
    [InlineData("Fixture.Filesystem")]
    [InlineData("fixture filesystem")]
    [InlineData("")]
    public void ValidateRejectsInvalidPluginIdentifiers(string plugin)
    {
        var value = new Phase4PluginLifecycleEvent
        {
            Plugin = plugin,
            Scope = Phase4PluginScope.Application,
            Transition = Phase4PluginLifecycleTransition.Created,
            Sequence = 0,
        };

        var action = () => Phase4PluginTestProtocol.Validate(value);

        action.Should().Throw<ArgumentException>();
    }
}