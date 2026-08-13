using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

using AwesomeAssertions;

using Nanto.WebView2InteropGen;

namespace Nanto.Hosting.Windows.InteropGeneration.IntegrationTests;

public sealed class InteropGenerationTests
{
    private const string ManifestFileName = "webview2-interop-manifest.json";
    private const string SourceFileName = "WebView2Interop.g.cs";

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
    public async Task ManifestHashesOfficialInputsAndGeneratedSource()
    {
        var paths = VerificationPaths.Load();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(paths.CommittedManifestPath, TestContext.Current.CancellationToken));
        var root = document.RootElement;
        root.GetProperty("packageVersion").GetString().Should().Be(new DirectoryInfo(paths.PackageRoot).Name);
        var inputs = root.GetProperty("inputs").EnumerateArray().ToDictionary(
            static item => item.GetProperty("path").GetString()!,
            static item => item.GetProperty("sha256").GetString()!,
            StringComparer.Ordinal);

        inputs["WebView2.idl"].Should().Be(Hash(await File.ReadAllBytesAsync(
            Path.Combine(paths.PackageRoot, "WebView2.idl"),
            TestContext.Current.CancellationToken)));
        inputs["build/native/include/WebView2.h"].Should().Be(Hash(await File.ReadAllBytesAsync(
            Path.Combine(paths.PackageRoot, "build", "native", "include", "WebView2.h"),
            TestContext.Current.CancellationToken)));
        inputs["eng/Nanto.WebView2InteropGen/webview2-interop-spec.json"].Should().Be(Hash(await File.ReadAllBytesAsync(
            paths.SpecPath,
            TestContext.Current.CancellationToken)));

        var sourceBytes = await File.ReadAllBytesAsync(paths.CommittedSourcePath, TestContext.Current.CancellationToken);
        root.GetProperty("output").GetProperty("sha256").GetString().Should().Be(Hash(sourceBytes));
        root.GetProperty("output").GetProperty("byteLength").GetInt32().Should().Be(sourceBytes.Length);
    }

    [Fact]
    public async Task ProjectionUsesOnlyApprovedGeneratedInteropModels()
    {
        var paths = VerificationPaths.Load();
        var source = await File.ReadAllTextAsync(paths.CommittedSourcePath, TestContext.Current.CancellationToken);

        source.Should().Contain("[GeneratedComInterface]");
        source.Should().Contain("[LibraryImport(");
        source.Should().Contain("ICoreWebView2Profile");
        source.Should().Contain("put_PreferredColorScheme");
        source.Should().Contain("ICoreWebView2ProcessFailedEventHandler");
        source.Should().Contain("ICoreWebView2ProcessFailedEventArgs");
        source.Should().Contain("COREWEBVIEW2_PROCESS_FAILED_KIND");
        source.Should().NotContain("[ComImport]");
        source.Should().NotContain("[DllImport(");
        source.Should().NotContain("dynamic ");
        source.Should().NotContain("System.Reflection");
    }

    [Fact]
    public void MissingOfficialInputFailsWithItsExactPath()
    {
        var paths = VerificationPaths.Load();
        var missingPackageRoot = Path.Combine(paths.VerificationDirectory, "missing-package");
        var output = Path.Combine(paths.VerificationDirectory, "missing-output");

        var action = () => Generator.Generate(missingPackageRoot, paths.SpecPath, output);

        action.Should().Throw<FileNotFoundException>().WithMessage($"*{Path.Combine(missingPackageRoot, "WebView2.idl")}*");
    }

    [Fact]
    public void IdlAndHeaderAbiDisagreementFailsClosed()
    {
        var paths = VerificationPaths.Load();
        paths.ResetVerificationDirectory();
        var packageRoot = CopyOfficialInputs(paths, "abi-mismatch");
        var headerPath = Path.Combine(packageRoot, "build", "native", "include", "WebView2.h");
        var header = File.ReadAllText(headerPath);
        header = header.Replace(
            "76eceacb-0462-4d94-ac83-423a6793775e",
            "00000000-0000-0000-0000-000000000000",
            StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(headerPath, header);

        var action = () => Generator.Generate(packageRoot, paths.SpecPath, Path.Combine(paths.VerificationDirectory, "abi-output"));

        action.Should().Throw<InvalidDataException>().WithMessage("*ABI*ICoreWebView2*");
    }

    [Fact]
    public void IdlAndHeaderParameterAbiDisagreementFailsClosed()
    {
        var paths = VerificationPaths.Load();
        paths.ResetVerificationDirectory();
        var packageRoot = CopyOfficialInputs(paths, "parameter-abi-mismatch");
        var headerPath = Path.Combine(packageRoot, "build", "native", "include", "WebView2.h");
        var header = File.ReadAllText(headerPath);
        var modifiedHeader = header.Replace("HWND parentWindow,", "UINT64 parentWindow,", StringComparison.Ordinal);
        modifiedHeader.Should().NotBe(header);
        File.WriteAllText(headerPath, modifiedHeader);

        var action = () => Generator.Generate(packageRoot, paths.SpecPath, Path.Combine(paths.VerificationDirectory, "parameter-abi-output"));

        action.Should().Throw<InvalidDataException>().WithMessage("*ABI*ICoreWebView2Environment*");
    }

    [Fact]
    public void UnknownSelectedInterfaceFailsClosed()
    {
        var paths = VerificationPaths.Load();
        paths.ResetVerificationDirectory();
        Directory.CreateDirectory(paths.VerificationDirectory);
        var specPath = Path.Combine(paths.VerificationDirectory, "unsupported-spec.json");
        var spec = File.ReadAllText(paths.SpecPath).Replace(
            "\"interfaces\": [",
            "\"interfaces\": [{ \"name\": \"ICoreWebView2DoesNotExist\", \"methods\": [\"Missing\"] },",
            StringComparison.Ordinal);
        File.WriteAllText(specPath, spec);

        var action = () => Generator.Generate(paths.PackageRoot, specPath, Path.Combine(paths.VerificationDirectory, "unsupported-output"));

        action.Should().Throw<InvalidDataException>().WithMessage("*ICoreWebView2DoesNotExist*");
    }

    [Fact]
    public void OutputPathThatIsAFileFailsWithoutModifyingIt()
    {
        var paths = VerificationPaths.Load();
        paths.ResetVerificationDirectory();
        Directory.CreateDirectory(paths.VerificationDirectory);
        var outputPath = Path.Combine(paths.VerificationDirectory, "blocked-output");
        File.WriteAllText(outputPath, "sentinel");

        var action = () => Generator.Generate(paths.PackageRoot, paths.SpecPath, outputPath);

        action.Should().Throw<IOException>();
        File.ReadAllText(outputPath).Should().Be("sentinel");
    }

    private static void AssertSameFile(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllBytes(expectedPath);
        var actual = File.ReadAllBytes(actualPath);
        actual.Should().Equal(expected, $"'{actualPath}' must reproduce '{expectedPath}' byte-for-byte");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string CopyOfficialInputs(VerificationPaths paths, string directoryName)
    {
        var packageRoot = Path.Combine(paths.VerificationDirectory, directoryName, new DirectoryInfo(paths.PackageRoot).Name);
        var headerDirectory = Path.Combine(packageRoot, "build", "native", "include");
        Directory.CreateDirectory(headerDirectory);
        File.Copy(Path.Combine(paths.PackageRoot, "WebView2.idl"), Path.Combine(packageRoot, "WebView2.idl"));
        File.Copy(Path.Combine(paths.PackageRoot, "build", "native", "include", "WebView2.h"), Path.Combine(headerDirectory, "WebView2.h"));
        return packageRoot;
    }

    private sealed record VerificationPaths(
        string RepositoryRoot,
        string PackageRoot,
        string VerificationDirectory)
    {
        public string SpecPath => Path.Combine(RepositoryRoot, "eng", "Nanto.WebView2InteropGen", "webview2-interop-spec.json");

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
            var metadata = typeof(InteropGenerationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            return new VerificationPaths(
                GetAbsolute(metadata, "Nanto.RepositoryRoot"),
                GetAbsolute(metadata, "Nanto.WebView2PackageRoot"),
                GetAbsolute(metadata, "Nanto.InteropVerificationDirectory"));
        }

        public void ResetVerificationDirectory()
        {
            var projectObj = Path.GetFullPath(Path.Combine(RepositoryRoot, "tests", "Nanto.Hosting.Windows.InteropGeneration.IntegrationTests", "obj"));
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