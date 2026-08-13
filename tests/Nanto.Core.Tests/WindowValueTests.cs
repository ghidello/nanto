using AwesomeAssertions;

namespace Nanto.Core.Tests;

public sealed class WindowValueTests
{
    [Fact]
    public void WindowIdRejectsEmptyGuid()
    {
        var action = () => new WindowId(Guid.Empty);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WindowIdCreateReturnsNonEmptyGuid()
    {
        WindowId.Create().Value.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(0, 100)]
    [InlineData(100, -1)]
    public void WindowSizeRejectsInvalidValues(double width, double height)
    {
        var action = () => new WindowSize(width, height);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
