using System.Collections.ObjectModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

using AwesomeAssertions;

namespace Nanto.Testing.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void TestingAssemblyReferencesOnlyPortableNonTestDependencies()
    {
        var forbiddenReferences = new[]
        {
            "AwesomeAssertions",
            "Microsoft.Maui",
            "Microsoft.Testing",
            "Microsoft.UI",
            "Microsoft.Web.WebView2",
            "Microsoft.Windows",
            "PresentationCore",
            "PresentationFramework",
            "System.Drawing",
            "System.Windows.Forms",
            "WindowsBase",
            "xunit",
        };
        var projectAssetsPath = Path.Combine(AppContext.BaseDirectory, "Nanto.Testing.project.assets.json");
        var references = typeof(FailurePlan).Assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Concat(ReadProjectDependencyNames(projectAssetsPath));

        references.Should().NotContain(name => forbiddenReferences.Any(forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TestingAssemblyDoesNotUseFilesystemThreadsOrWallClockDelays()
    {
        var memberReferences = ReadMemberReferences(typeof(FailurePlan).Assembly.Location);

        memberReferences.Should().NotContain(reference => reference.StartsWith("System.IO.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference => reference.StartsWith("System.Threading.Thread.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference => reference.StartsWith("System.Threading.ThreadPool.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference => reference.StartsWith("System.Threading.Timer.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference => reference.StartsWith("System.Threading.PeriodicTimer.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference => reference.StartsWith("System.Timers.Timer.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference => reference.StartsWith("System.Diagnostics.Stopwatch.", StringComparison.Ordinal));
        memberReferences.Should().NotContain(reference =>
            reference == "System.Threading.Tasks.Task.Delay" || reference == "System.Threading.Tasks.Task.Run");
        memberReferences.Should().NotContain(reference => reference == "System.Threading.Tasks.TaskFactory.StartNew");
        memberReferences.Should().NotContain("System.TimeProvider.get_System");
    }

    private static ReadOnlyCollection<string> ReadProjectDependencyNames(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var dependencies = new List<string>();
        foreach (var dependency in document.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var versionSeparator = dependency.Name.LastIndexOf('/');
            dependencies.Add(versionSeparator >= 0 ? dependency.Name[..versionSeparator] : dependency.Name);
        }

        foreach (var framework in document.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("dependencies", out var directDependencies))
            {
                dependencies.AddRange(directDependencies.EnumerateObject().Select(static dependency => dependency.Name));
            }

            if (framework.Value.TryGetProperty("frameworkReferences", out var frameworkReferences))
            {
                dependencies.AddRange(frameworkReferences.EnumerateObject().Select(static reference => reference.Name));
            }
        }

        return Array.AsReadOnly([.. dependencies]);
    }

    private static ReadOnlyCollection<string> ReadMemberReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var portableExecutable = new PEReader(stream);
        var metadata = portableExecutable.GetMetadataReader();
        var references = new List<string>();

        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var declaringType = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            var typeNamespace = metadata.GetString(declaringType.Namespace);
            var typeName = metadata.GetString(declaringType.Name);
            var memberName = metadata.GetString(member.Name);
            references.Add($"{typeNamespace}.{typeName}.{memberName}");
        }

        return Array.AsReadOnly([.. references]);
    }
}