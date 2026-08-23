using AwesomeAssertions;

using Nanto.Cli.Diagnostics;

namespace Nanto.Cli.Tests;

public sealed class NantoDoctorTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not-a-version", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("142.0.3595.94", true)]
    public void WebView2RuntimeRequiresAValidNonZeroProductVersion(string? value, bool expected)
    {
        NantoDoctor.IsInstalledWebView2Version(value).Should().Be(expected);
    }
}
