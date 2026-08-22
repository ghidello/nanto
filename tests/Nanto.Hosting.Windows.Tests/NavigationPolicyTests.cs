using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class NavigationPolicyTests
{
    private static readonly IReadOnlySet<string> _assets = new HashSet<string>(
        ["/index.html", "/assets/app.js", "/café.html"],
        StringComparer.Ordinal);

    [Theory]
    [InlineData("https://app.nanto.invalid/index.html")]
    [InlineData("https://APP.NANTO.INVALID:443/assets/app.js?v=1#module")]
    [InlineData("https://app.nanto.invalid/caf%C3%A9.html")]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/path")]
    [InlineData("https://[::1]/path")]
    public void IsAllowedAcceptsDeclaredOrExternalHttpNavigation(string candidate)
    {
        NavigationPolicy.IsAllowed(candidate, _assets).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://app.nanto.invalid/")]
    [InlineData("https://app.nanto.invalid/missing")]
    [InlineData("https://app.nanto.invalid/INDEX.HTML")]
    [InlineData("http://app.nanto.invalid/index.html")]
    [InlineData("https://app.nanto.invalid:444/index.html")]
    [InlineData("https://user@app.nanto.invalid/index.html")]
    [InlineData("https://user@example.com/index.html")]
    [InlineData("https://app.nanto.invalid./index.html")]
    [InlineData("https://app.nanto.invalid/%2e%2e/secret")]
    [InlineData("https://app.nanto.invalid/assets%2Fapp.js")]
    [InlineData("https://app.nanto.invalid/%252e%252e/secret")]
    [InlineData("https://app.nanto.invalid/cafe%CC%81.html")]
    [InlineData("https://app.nanto.invalid/%FF.html")]
    [InlineData("file:///c:/secret")]
    [InlineData("data:text/plain,secret")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a URI")]
    [InlineData("https://example.com/%ZZ")]
    [InlineData("https://exa mple.com/path")]
    public void IsAllowedRejectsUnsafeOrUndeclaredNavigation(string candidate)
    {
        NavigationPolicy.IsAllowed(candidate, _assets).Should().BeFalse();
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/index.html?route=settings#profile")]
    public void IsInitialRouteAllowedUsesDeclaredPath(string route)
    {
        NavigationPolicy.IsInitialRouteAllowed(route, _assets).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://LOCALHOST:5173/deep/route?query=1")]
    [InlineData("http://localhost:5173/assets/app.js")]
    public void DevelopmentContentAllowsAnyPathOnConfiguredOrigin(string candidate)
    {
        var content = new PreparedWindowsContent
        {
            StartUri = new Uri("http://localhost:5173/"),
            TrustedOrigin = new Uri("http://localhost:5173/"),
            AssetPaths = new HashSet<string>(StringComparer.Ordinal),
        };

        NavigationPolicy.IsAllowed(candidate, content).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://localhost:5173/path", true)]
    [InlineData("http://LOCALHOST:5173/path", true)]
    [InlineData("http://127.0.0.1:5173/path", false)]
    [InlineData("http://localhost:5174/path", false)]
    [InlineData("https://localhost:5173/path", false)]
    [InlineData("http://user@localhost:5173/path", false)]
    [InlineData("file:///c:/path", false)]
    public void IsTrustedOriginRequiresExactSchemeHostAndPort(string candidate, bool expected)
    {
        NavigationPolicy.IsTrustedOrigin(candidate, new Uri("http://localhost:5173/")).Should().Be(expected);
    }
}
