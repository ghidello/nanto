using System.Diagnostics;
using System.Reflection;

using AwesomeAssertions;

using Nanto.Hosting.Windows.IntegrationTestKit;
using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.AotIntegrationTests;

public sealed class NativeAotDeploymentTests
{
    private const string Mode = "NativeAot";
    private const string RuntimeIdentifier = "win-x64";
    private const string TargetFramework = "net10.0";

    private static readonly Phase1TestScenario[] _smokeScenarios =
    [
        Phase1TestScenario.HostLifecycle,
        Phase1TestScenario.Navigation,
        Phase1TestScenario.RendererRecovery,
    ];

    [Fact]
    public async Task PublishedDeploymentIsNativeStaticallyLinkedAndPassesCriticalSmokes()
    {
        var repositoryRoot = GetAssemblyMetadata("NantoRepositoryRoot");
        var publishDirectory = GetAssemblyMetadata("NantoPublishDirectory");
        var symbolDirectory = GetAssemblyMetadata("NantoSymbolDirectory");
        var packageDirectory = GetAssemblyMetadata("NantoPackageDirectory");
        var evidencePath = GetAssemblyMetadata("NantoEvidencePath");
        var executablePath = Path.Combine(publishDirectory, "Nanto.Hosting.Windows.TestApp.exe");

        var deployment = Phase1DeploymentInspector.InspectDirectory(publishDirectory);
        var symbols = Phase1DeploymentInspector.InspectDirectory(symbolDirectory);
        var architecture = Phase1DeploymentInspector.ReadPortableExecutableArchitecture(executablePath);
        var imports = Phase1DeploymentInspector.ReadPortableExecutableImports(executablePath);
        AssertDeploymentStructure(deployment, symbols, architecture, imports, executablePath);

        var smokeResults = new List<Phase1DeploymentSmokeResult>();
        foreach (var scenario in _smokeScenarios)
        {
            smokeResults.Add(await RunSmokeAsync(repositoryRoot, executablePath, scenario));
        }

        var packagePath = Path.Combine(packageDirectory, "Nanto.Hosting.Windows.TestApp-win-x64.zip");
        var package = Phase1DeploymentInspector.CreateDeterministicPackage(publishDirectory, packagePath, deployment.Files);
        var repeatedPackage = Phase1DeploymentInspector.CreateDeterministicPackage(publishDirectory, packagePath, deployment.Files);
        repeatedPackage.Should().Be(package, "packaging the same deployment twice must reproduce identical bytes");
        var evidence = new Phase1DeploymentEvidence
        {
            Mode = Mode,
            RuntimeIdentifier = RuntimeIdentifier,
            TargetFramework = TargetFramework,
            SdkVersion = GetAssemblyMetadata("NantoSdkVersion"),
            Architecture = architecture,
            DeploymentFiles = deployment.Files,
            TotalDeployedBytes = deployment.TotalBytes,
            DeclaredEmbeddedAssetBytes = Phase1DeploymentInspector.ReadDeclaredAssetBytes(
                Path.Combine(repositoryRoot, "tests", "Nanto.Hosting.Windows.TestApp", "WebAssets", "nanto-assets.json")),
            PackageRelativePath = Path.GetRelativePath(repositoryRoot, packagePath).Replace('\\', '/'),
            PackageLength = package.Length,
            PackageSha256 = package.Sha256,
            SymbolFiles = symbols.Files,
            TotalSymbolBytes = symbols.TotalBytes,
            LoaderForm = "StaticallyLinkedLibrary",
            LoaderSignatureTrusted = null,
            LoaderCompanyName = null,
            ExecutableImports = imports,
            SmokeScenarios = smokeResults.ToArray(),
        };
        Phase1DeploymentInspector.WriteEvidence(evidencePath, evidence);

        File.Exists(evidencePath).Should().BeTrue();
    }

    private static void AssertDeploymentStructure(
        Phase1DeploymentInspection deployment,
        Phase1DeploymentInspection symbols,
        string architecture,
        IReadOnlyCollection<string> imports,
        string executablePath)
    {
        File.Exists(executablePath).Should().BeTrue();
        architecture.Should().Be("X64");
        deployment.Files.Should().ContainSingle().Which.RelativePath.Should().Be("Nanto.Hosting.Windows.TestApp.exe");
        Phase1DeploymentInspector.HasManagedMetadata(executablePath)
            .Should().BeFalse("the Native AOT executable must not retain managed metadata");
        imports.Should().NotContain(importName => string.Equals(importName, "WebView2Loader.dll", StringComparison.OrdinalIgnoreCase));
        imports.Should().NotContain(importName => string.Equals(importName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));

        symbols.Files.Should().Contain(file => file.RelativePath == "native/Nanto.Hosting.Windows.TestApp.pdb");
        symbols.Files.Should().OnlyContain(static file => file.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        deployment.Files.Should().NotContain(static file =>
            file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetAssemblyMetadata(string name)
    {
        return typeof(NativeAotDeploymentTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == name)
            .Value ?? throw new InvalidOperationException($"Assembly metadata '{name}' does not have a value.");
    }

    private static async Task<Phase1DeploymentSmokeResult> RunSmokeAsync(
        string repositoryRoot,
        string executablePath,
        Phase1TestScenario scenario)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await Phase1TestProcessRunner.RunAsync(
            new Phase1TestRunOptions
            {
                TestAppPath = executablePath,
                ArtifactRoot = Path.Combine(repositoryRoot, "artifacts", "phase1", "runs", "native-aot"),
                Scenario = scenario,
                Timeout = TimeSpan.FromSeconds(45),
            },
            TestContext.Current.CancellationToken);
        stopwatch.Stop();

        result.Succeeded.Should().BeTrue(
            "the {0} smoke scenario must succeed; stdout: {1}; stderr: {2}; artifacts: {3}",
            scenario,
            result.StandardOutput,
            result.StandardError,
            result.ArtifactDirectory);
        result.ArtifactsRetained.Should().BeFalse();
        result.Report.Should().NotBeNull();
        result.Report!.FinalResources.TotalActive.Should().Be(0);
        Directory.Exists(result.ApplicationRoot).Should().BeFalse("the deployment smoke must leave application storage deletable");

        return new Phase1DeploymentSmokeResult
        {
            Scenario = scenario.ToString(),
            Succeeded = result.Succeeded,
            ElapsedMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds,
            ExitCode = result.ExitCode,
            FinalActiveResourceCount = result.Report.FinalResources.TotalActive,
        };
    }
}
