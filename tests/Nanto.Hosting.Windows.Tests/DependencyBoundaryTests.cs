using System.Text.Json;

using AwesomeAssertions;

namespace Nanto.Hosting.Windows.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void WindowsHostGraphDoesNotReferenceUiFrameworks()
    {
        var forbiddenReferences = new[]
        {
            "Microsoft.Maui",
            "Microsoft.UI.Xaml",
            "Microsoft.WindowsAppSDK",
            "Microsoft.WindowsDesktop.App",
            "PresentationFramework",
            "System.Windows.Forms",
        };
        var projectAssetsPath = Path.Combine(AppContext.BaseDirectory, "Nanto.Hosting.Windows.project.assets.json");
        var references = typeof(WindowsHostAssembly).Assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Concat(ReadProjectDependencyNames(projectAssetsPath));

        references.Should().NotContain(name => forbiddenReferences.Any(forbidden => name.StartsWith(forbidden, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> ReadProjectDependencyNames(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var dependency in document.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var versionSeparator = dependency.Name.LastIndexOf('/');
            yield return versionSeparator >= 0 ? dependency.Name[..versionSeparator] : dependency.Name;
        }

        foreach (var framework in document.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    yield return dependency.Name;
                }
            }

            if (framework.Value.TryGetProperty("frameworkReferences", out var frameworkReferences))
            {
                foreach (var reference in frameworkReferences.EnumerateObject())
                {
                    yield return reference.Name;
                }
            }
        }
    }
}