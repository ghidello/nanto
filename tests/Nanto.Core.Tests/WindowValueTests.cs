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
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(0, double.PositiveInfinity, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, 100, -1)]
    public void WindowBoundsRejectInvalidValues(double x, double y, double width, double height)
    {
        var action = () => new WindowBounds(x, y, width, height);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WindowBoundsAcceptNegativeMonitorCoordinates()
    {
        var bounds = new WindowBounds(-1920, -200, 1024, 768);

        bounds.X.Should().Be(-1920);
        bounds.Y.Should().Be(-200);
    }
}