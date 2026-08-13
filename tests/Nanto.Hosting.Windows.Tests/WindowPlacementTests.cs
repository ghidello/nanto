using AwesomeAssertions;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void PartiallyVisibleWindowIsNotMoved()
    {
        var workAreas = new[] { new RECT(0, 0, 1920, 1040) };

        var corrected = WindowPlacement.TryGetCorrection(new RECT(-799, 100, 1, 700), workAreas, out _);

        corrected.Should().BeFalse();
    }

    [Fact]
    public void InaccessibleWindowIsClampedToTheNearestWorkAreaWithoutChangingItsSize()
    {
        var workAreas = new[]
        {
            new RECT(-1920, 0, 0, 1040),
            new RECT(0, 0, 1920, 1040),
        };

        var corrected = WindowPlacement.TryGetCorrection(new RECT(2200, 900, 3000, 1500), workAreas, out var position);

        corrected.Should().BeTrue();
        position.Should().Be(new WindowPlacement.NativePosition(1120, 440));
    }

    [Fact]
    public void OversizedWindowAlignsWithTheNearestWorkAreaOrigin()
    {
        var workAreas = new[] { new RECT(-1600, -200, 0, 700) };

        var corrected = WindowPlacement.TryGetCorrection(new RECT(2000, 1000, 4000, 2200), workAreas, out var position);

        corrected.Should().BeTrue();
        position.Should().Be(new WindowPlacement.NativePosition(-1600, -200));
    }

    [Fact]
    public void EquidistantWorkAreasUseStableCoordinateOrdering()
    {
        var workAreas = new[]
        {
            new RECT(100, 0, 200, 100),
            new RECT(-200, 0, -100, 100),
        };

        var corrected = WindowPlacement.TryGetCorrection(new RECT(-50, 0, 50, 100), workAreas, out var position);

        corrected.Should().BeTrue();
        position.Should().Be(new WindowPlacement.NativePosition(-200, 0));
    }

    [Fact]
    public void MissingWorkAreasFailClosed()
    {
        var correct = () => WindowPlacement.TryGetCorrection(new RECT(0, 0, 100, 100), [], out _);

        correct.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, 100, 0)]
    public void InvalidRectanglesAreRejected(int left, int top, int right, int bottom)
    {
        var correct = () => WindowPlacement.TryGetCorrection(
            new RECT(left, top, right, bottom),
            [new RECT(0, 0, 100, 100)],
            out _);

        correct.Should().Throw<ArgumentOutOfRangeException>();
    }
}