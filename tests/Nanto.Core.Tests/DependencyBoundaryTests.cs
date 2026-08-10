using AwesomeAssertions;

namespace Nanto.Core.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void CoreDoesNotReferencePlatformFrameworks()
    {
        var forbiddenReferences = new[]
        {
            "Microsoft.Maui",
            "Microsoft.UI",
            "Microsoft.Web.WebView2",
            "PresentationFramework",
            "System.Windows.Forms",
        };
        var references = typeof(WindowId).Assembly.GetReferencedAssemblies().Select(static reference => reference.Name ?? string.Empty);

        references.Should().NotContain(name => forbiddenReferences.Any(forbidden => name.StartsWith(forbidden, StringComparison.Ordinal)));
    }
}