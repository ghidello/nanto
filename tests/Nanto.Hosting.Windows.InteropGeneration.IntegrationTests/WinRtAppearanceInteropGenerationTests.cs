using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

using AwesomeAssertions;

using Nanto.WinRtAppearanceInteropGen;

namespace Nanto.Hosting.Windows.InteropGeneration.IntegrationTests;

public sealed class WinRtAppearanceInteropGenerationTests
{
    private const string ManifestFileName = "winrt-appearance-interop-manifest.json";
    private const string SourceFileName = "WinRtAppearanceInterop.g.cs";

    [Fact]
    public void RegenerationIsDeterministicAndMatchesCommittedOutputs()
    {
        var paths = VerificationPaths.Load();
        paths.ResetVerificationDirectory();
        var first = Path.Combine(paths.VerificationDirectory, "Generated");
        var second = Path.Combine(paths.VerificationDirectory, "Determinism", "Generated");

        Generator.Generate(paths.PackageRoot, paths.SpecPath, first);
        Generator.Generate(paths.PackageRoot, paths.SpecPath, second);

        AssertSameFile(Path.Combine(first, SourceFileName), Path.Combine(second, SourceFileName));
        AssertSameFile(Path.Combine(first, ManifestFileName), Path.Combine(second, ManifestFileName));
        AssertSameFile(paths.CommittedSourcePath, Path.Combine(first, SourceFileName));
        AssertSameFile(paths.CommittedManifestPath, Path.Combine(first, ManifestFileName));
    }

    [Fact]
    public async Task ManifestHashesPinnedMetadataAndGeneratedSource()
    {
        var paths = VerificationPaths.Load();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(paths.CommittedManifestPath, TestContext.Current.CancellationToken));
        var root = document.RootElement;
        root.GetProperty("targetingPackVersion").GetString().Should().Be("10.0.19041.57");
        root.GetProperty("interface").GetProperty("iid").GetString().Should().Be("03021be4-5254-4781-8194-5168f7d06d7b");
        root.GetProperty("eventHandler").GetProperty("iid").GetString().Should().Be("2dbdba9d-20da-519d-9078-09f835bc5bc7");

        var sourceBytes = await File.ReadAllBytesAsync(paths.CommittedSourcePath, TestContext.Current.CancellationToken);
        root.GetProperty("output").GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(sourceBytes)));
        root.GetProperty("output").GetProperty("byteLength").GetInt32().Should().Be(sourceBytes.Length);
        sourceBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }).Should().BeFalse();
        sourceBytes.Should().NotContain(0x0D);
    }

    [Fact]
    public async Task ProjectionContainsOnlyTheApprovedRawInspectableSurface()
    {
        var paths = VerificationPaths.Load();
        var source = await File.ReadAllTextAsync(paths.CommittedSourcePath, TestContext.Current.CancellationToken);

        source.Should().Contain("namespace Nanto.Hosting.Windows.Interop;");
        source.Should().Contain("RawWinRtAbi");
        source.Should().Contain("GetColorValue");
        source.Should().Contain("AddColorValuesChanged");
        source.Should().Contain("RemoveColorValuesChanged");
        source.Should().Contain("RawColorValuesChangedHandler");
        source.Should().NotContain("[GeneratedComInterface]");
        source.Should().NotContain("WinRT.Runtime");
        source.Should().NotContain("Windows.UI.ViewManagement.UISettings");
    }

    [Fact]
    public async Task ProductionSourcesDoNotUseTheSdkWinRtProjection()
    {
        var paths = VerificationPaths.Load();
        var productionRoot = Path.Combine(paths.RepositoryRoot, "src");
        var appearanceSourcePath = Path.Combine(productionRoot, "Nanto.Hosting.Windows", "WindowsSystemAppearanceSource.cs");
        var productionFiles = Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var path in productionFiles)
        {
            var source = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            source.Should().NotContain("using Windows.UI.", because: path);
            source.Should().NotContain("using WinRT;", because: path);
            source.Should().NotContain("IWinRTObject", because: path);
            source.Should().NotContain("WinRT.Runtime", because: path);

            var windowsUiOccurrences = source.Split("Windows.UI.", StringSplitOptions.None).Length - 1;
            windowsUiOccurrences.Should().Be(
                string.Equals(path, appearanceSourcePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                because: $"only the UISettings runtime-class string is permitted in production source; inspected '{path}'");
        }

        foreach (var path in Directory.EnumerateFiles(productionRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var project = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            project.Should().NotContain("WinRT.Runtime", because: path);
            project.Should().NotContain("Microsoft.Windows.SDK.NET.Ref", because: path);
        }
    }

    [Fact]
    public void MismatchedTargetingPackFailsClosed()
    {
        var paths = VerificationPaths.Load();
        paths.ResetVerificationDirectory();
        var mismatchedRoot = Path.Combine(paths.VerificationDirectory, "10.0.19041.999");
        Directory.CreateDirectory(mismatchedRoot);

        var action = () => Generator.Generate(mismatchedRoot, paths.SpecPath, Path.Combine(paths.VerificationDirectory, "output"));

        action.Should().Throw<InvalidDataException>().WithMessage("*does not match*");
    }

    private static void AssertSameFile(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllBytes(expectedPath);
        var actual = File.ReadAllBytes(actualPath);
        actual.Should().Equal(expected, $"'{actualPath}' must reproduce '{expectedPath}' byte-for-byte");
    }

    private sealed record VerificationPaths(string RepositoryRoot, string PackageRoot, string VerificationDirectory)
    {
        public string SpecPath => Path.Combine(RepositoryRoot, "eng", "Nanto.WinRtAppearanceInteropGen", "winrt-appearance-spec.json");

        public string CommittedSourcePath => Path.Combine(
            RepositoryRoot,
            "src",
            "Nanto.Hosting.Windows",
            "Interop",
            "Generated",
            SourceFileName);

        public string CommittedManifestPath => Path.Combine(
            RepositoryRoot,
            "src",
            "Nanto.Hosting.Windows",
            "Interop",
            "Generated",
            ManifestFileName);

        public static VerificationPaths Load()
        {
            var metadata = typeof(WinRtAppearanceInteropGenerationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            return new VerificationPaths(
                GetAbsolute(metadata, "Nanto.RepositoryRoot"),
                GetAbsolute(metadata, "Nanto.WindowsSdkPackageRoot"),
                Path.Combine(GetAbsolute(metadata, "Nanto.InteropVerificationDirectory"), "winrt-appearance"));
        }

        public void ResetVerificationDirectory()
        {
            var projectObj = Path.GetFullPath(Path.Combine(
                RepositoryRoot,
                "tests",
                "Nanto.Hosting.Windows.InteropGeneration.IntegrationTests",
                "obj"));
            var relative = Path.GetRelativePath(projectObj, VerificationDirectory);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            {
                throw new InvalidOperationException($"Verification directory '{VerificationDirectory}' is outside '{projectObj}'.");
            }

            if (Directory.Exists(VerificationDirectory))
            {
                Directory.Delete(VerificationDirectory, recursive: true);
            }
        }

        private static string GetAbsolute(Dictionary<string, string?> metadata, string key)
        {
            if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                throw new InvalidOperationException($"Assembly metadata '{key}' must contain one absolute path.");
            }

            return Path.GetFullPath(value);
        }
    }
}
