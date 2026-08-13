using System.Diagnostics;
using System.Reflection;

using AwesomeAssertions;

using Nanto.Hosting.Windows.IntegrationTestKit;
using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.CoreClrSelfContainedIntegrationTests;

public sealed class CoreClrSelfContainedDeploymentTests
{
    private const string Mode = "CoreClrSelfContained";
    private const string RuntimeIdentifier = "win-x64";
    private const string TargetFramework = "net10.0";

    private static readonly string[] _forbiddenFileNameFragments =
    [
        "Microsoft.Maui",
        "Microsoft.UI.Xaml",
        "Microsoft.Web.WebView2.Core",
        "Microsoft.Web.WebView2.WinForms",
        "Microsoft.Web.WebView2.Wpf",
        "Microsoft.WindowsAppRuntime",
        "PresentationFramework",
        "System.Windows.Forms",
        "WinUI",
    ];

    private static readonly Phase1TestScenario[] _smokeScenarios =
    [
        Phase1TestScenario.HostLifecycle,
        Phase1TestScenario.Navigation,
        Phase1TestScenario.RendererRecovery,
    ];

    [Fact]
    public async Task PublishedDeploymentIsSelfContainedTrustedAndPassesCriticalSmokes()
    {
        var repositoryRoot = GetAssemblyMetadata("NantoRepositoryRoot");
        var publishDirectory = GetAssemblyMetadata("NantoPublishDirectory");
        var symbolDirectory = GetAssemblyMetadata("NantoSymbolDirectory");
        var packageDirectory = GetAssemblyMetadata("NantoPackageDirectory");
        var evidencePath = GetAssemblyMetadata("NantoEvidencePath");
        var executablePath = Path.Combine(publishDirectory, "Nanto.Hosting.Windows.TestApp.exe");
        var dependencyManifestPath = Path.Combine(publishDirectory, "Nanto.Hosting.Windows.TestApp.deps.json");
        var runtimeConfigurationPath = Path.Combine(publishDirectory, "Nanto.Hosting.Windows.TestApp.runtimeconfig.json");
        var loaderPath = Path.Combine(publishDirectory, "WebView2Loader.dll");

        var deployment = Phase1DeploymentInspector.InspectDirectory(publishDirectory);
        var symbols = Phase1DeploymentInspector.InspectDirectory(symbolDirectory);
        var deploymentIdentity = Phase1DeploymentInspector.ReadSelfContainedDeploymentIdentity(
            executablePath,
            runtimeConfigurationPath,
            dependencyManifestPath);
        AssertDeploymentStructure(deployment, deploymentIdentity, executablePath, dependencyManifestPath, loaderPath);
        symbols.Files.Should().NotBeEmpty().And.OnlyContain(static file => file.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));

        var smokeResults = new List<Phase1DeploymentSmokeResult>();
        foreach (var scenario in _smokeScenarios)
        {
            smokeResults.Add(await RunSmokeAsync(repositoryRoot, executablePath, scenario));
        }

        var packagePath = Path.Combine(packageDirectory, "Nanto.Hosting.Windows.TestApp-win-x64.zip");
        var package = Phase1DeploymentInspector.CreateDeterministicPackage(publishDirectory, packagePath, deployment.Files);
        var repeatedPackage = Phase1DeploymentInspector.CreateDeterministicPackage(publishDirectory, packagePath, deployment.Files);
        repeatedPackage.Should().Be(package, "packaging the same deployment twice must reproduce identical bytes");
        var imports = Phase1DeploymentInspector.ReadPortableExecutableImports(executablePath);
        var loaderTrusted = Phase1DeploymentInspector.IsAuthenticodeSignatureTrusted(loaderPath);
        var loaderCompanyName = Phase1DeploymentInspector.GetCompanyName(loaderPath);
        var evidence = new Phase1DeploymentEvidence
        {
            Mode = Mode,
            RuntimeIdentifier = deploymentIdentity.RuntimeIdentifier,
            TargetFramework = deploymentIdentity.TargetFramework,
            SdkVersion = GetAssemblyMetadata("NantoSdkVersion"),
            Architecture = deploymentIdentity.Architecture,
            DeploymentFiles = deployment.Files,
            TotalDeployedBytes = deployment.TotalBytes,
            DeclaredEmbeddedAssetBytes = Phase1DeploymentInspector.ReadDeclaredAssetBytes(
                Path.Combine(repositoryRoot, "tests", "Nanto.Hosting.Windows.TestApp", "WebAssets", "nanto-assets.json")),
            PackageRelativePath = Path.GetRelativePath(repositoryRoot, packagePath).Replace('\\', '/'),
            PackageLength = package.Length,
            PackageSha256 = package.Sha256,
            SymbolFiles = symbols.Files,
            TotalSymbolBytes = symbols.TotalBytes,
            LoaderForm = "AdjacentDynamicLibrary",
            LoaderSignatureTrusted = loaderTrusted,
            LoaderCompanyName = loaderCompanyName,
            ExecutableImports = imports,
            SmokeScenarios = smokeResults.ToArray(),
        };
        Phase1DeploymentInspector.WriteEvidence(evidencePath, evidence);

        loaderTrusted.Should().BeTrue("Windows must trust the adjacent WebView2 loader's Authenticode signature");
        loaderCompanyName.Should().Be("Microsoft Corporation", "the adjacent loader must identify Microsoft in its version metadata");
        File.Exists(evidencePath).Should().BeTrue();
    }

    private static void AssertDeploymentStructure(
        Phase1DeploymentInspection deployment,
        Phase1DeploymentIdentity deploymentIdentity,
        string executablePath,
        string dependencyManifestPath,
        string loaderPath)
    {
        File.Exists(executablePath).Should().BeTrue();
        File.Exists(loaderPath).Should().BeTrue();
        Directory.EnumerateDirectories(Path.GetDirectoryName(executablePath)!, "*", SearchOption.AllDirectories).Should().BeEmpty();
        deploymentIdentity.Should().Be(new Phase1DeploymentIdentity
        {
            TargetFramework = TargetFramework,
            RuntimeIdentifier = RuntimeIdentifier,
            Architecture = "X64",
        });

        var fileNames = deployment.Files.Select(static file => file.RelativePath).ToArray();
        fileNames.Should().Contain("coreclr.dll");
        fileNames.Should().Contain("hostfxr.dll");
        fileNames.Should().Contain("hostpolicy.dll");
        fileNames.Should().Contain("Nanto.Hosting.Windows.TestApp.deps.json");
        fileNames.Should().Contain("Nanto.Hosting.Windows.TestApp.runtimeconfig.json");
        fileNames.Should().Contain("WebView2Loader.dll");
        fileNames.Should().NotContain(static file => file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        Phase1DeploymentInspector.HasManagedMetadata(Path.Combine(Path.GetDirectoryName(executablePath)!, "Nanto.Hosting.Windows.TestApp.dll"))
            .Should().BeTrue("self-contained CoreCLR retains the managed TestApp assembly rather than compiling it as Native AOT");
        File.ReadAllText(dependencyManifestPath)
            .Should().Contain("runtimepack.Microsoft.NETCore.App.Runtime.win-x64");
        foreach (var fragment in _forbiddenFileNameFragments)
        {
            fileNames.Should().NotContain(file => file.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string GetAssemblyMetadata(string name)
    {
        return typeof(CoreClrSelfContainedDeploymentTests).Assembly
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
                ArtifactRoot = Path.Combine(repositoryRoot, "artifacts", "phase1", "runs", "coreclr-self-contained"),
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
