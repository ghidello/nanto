using AwesomeAssertions;

using Nanto.Hosting;

namespace Nanto.Core.Tests;

public sealed class ApplicationIdentityTests
{
    [Fact]
    public void ParseCanonicalizesAndCreatesStableReadableKey()
    {
        var first = ApplicationIdentity.Parse("  Com.Example.My-App  ");
        var second = ApplicationIdentity.Parse("com.example.my-app");

        first.Should().Be(second);
        first.CanonicalId.Should().Be("com.example.my-app");
        first.StorageKey.Should().MatchRegex("^my-app-[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData("single")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("com.-nanto")]
    [InlineData("com.nanto-")]
    [InlineData("com.nan_to")]
    [InlineData("cöm.nanto")]
    public void ParseRejectsInvalidIdentifiers(string applicationId)
    {
        var action = () => ApplicationIdentity.Parse(applicationId);

        action.Should().Throw<ArgumentException>();
    }
}
