using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class DpiConversionsTests
{
    [Theory]
    [InlineData(100, 96, 100)]
    [InlineData(100, 120, 125)]
    [InlineData(100, 144, 150)]
    [InlineData(101, 120, 126)]
    public void ToPixelsUsesTheSuppliedMonitorDpi(double dips, uint dpi, int expectedPixels)
    {
        DpiConversions.ToPixels(dips, dpi, nameof(dips)).Should().Be(expectedPixels);
    }

    [Theory]
    [InlineData(100, 96, 100)]
    [InlineData(125, 120, 100)]
    [InlineData(150, 144, 100)]
    public void ToDipsUsesTheSuppliedMonitorDpi(int pixels, uint dpi, double expectedDips)
    {
        DpiConversions.ToDips(pixels, dpi).Should().Be(expectedDips);
    }

    [Fact]
    public void ToPixelsRejectsNativeOverflow()
    {
        var convert = () => DpiConversions.ToPixels(double.MaxValue, 144, "value");

        convert.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConversionRejectsAnInvalidDpi()
    {
        var toPixels = () => DpiConversions.ToPixels(100, 0, "value");
        var toDips = () => DpiConversions.ToDips(100, 0);

        toPixels.Should().Throw<ArgumentOutOfRangeException>();
        toDips.Should().Throw<ArgumentOutOfRangeException>();
    }
}
