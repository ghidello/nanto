using AwesomeAssertions;

namespace Nanto.Core.Tests;

public sealed class WebAssetPathTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("//index.html")]
    [InlineData("/assets/")]
    [InlineData("/assets//app.js")]
    [InlineData("/./index.html")]
    [InlineData("/../index.html")]
    [InlineData("/assets\\app.js")]
    [InlineData("/assets%2Fapp.js")]
    [InlineData("/C:/index.html")]
    [InlineData("/index.html?query")]
    [InlineData("/index.html#fragment")]
    [InlineData("/control\u0001.html")]
    [InlineData("/cafe\u0301.html")]
    [InlineData("/nanto-assets.json")]
    [InlineData("/NANTO-ASSETS.JSON")]
    public void NormalizeManifestPathRejectsUnsafeOrAmbiguousPaths(string path)
    {
        var action = () => WebAssetPath.NormalizeManifestPath(path);

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void NormalizeManifestPathRejectsEmptyPath()
    {
        var action = () => WebAssetPath.NormalizeManifestPath(string.Empty);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NormalizeManifestPathAcceptsNormalizedUnicode()
    {
        WebAssetPath.NormalizeManifestPath("/café.html").Should().Be("/café.html");
    }
}