using System.Reflection;

using AwesomeAssertions;

using Nanto.Hosting.Windows.IntegrationTestKit;
using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.VisibleIntegrationTests;

public sealed class VisibleDesktopAcceptanceTests
{
    private static readonly string _repositoryRoot = GetAssemblyMetadata("NantoRepositoryRoot");
    private static readonly string _testAppPath = GetAssemblyMetadata("NantoTestAppPath");
    private static readonly string _artifactRoot = Path.Combine(_repositoryRoot, "artifacts", "phase1", "visible");

    [Fact]
    public async Task VisibleDesktopExercisesFocusSizingAppearanceKeyboardAndScreenshots()
    {
        var result = await RunAsync(Phase1TestScenario.VisibleDesktop);
        var report = RequireSuccessfulReport(result);

        report.VisibleAcceptanceStatus.Should().Be(Phase1VisibleAcceptanceStatus.Passed);
        report.MonitorTopology.Should().NotBeEmpty();
        report.WindowObservations.Select(static observation => observation.Stage).Should().Contain([
            "System",
            "Resized",
            "Dark",
            "Light"]);
        report.WindowObservations.Single(static observation => observation.Stage == "Resized").Should().Match<Phase1WindowObservation>(
            static observation => observation.ClientWidth == 900 && observation.ClientHeight == 600);
        report.WindowObservations.Single(static observation => observation.Stage == "System").Should().Match<Phase1WindowObservation>(
            static observation => observation.IsForeground && observation.HasKeyboardFocus);
        AssertRetainedArtifacts(result, report);
        report.FinalResources.TotalActive.Should().Be(0);
    }

    [Fact]
    public async Task VisibleWindowTraversesTwoMonitorsWithDifferentEffectiveDpi()
    {
        var result = await RunAsync(Phase1TestScenario.VisibleCrossMonitorDpi);
        var report = RequireSuccessfulReport(result);
        AssertRetainedArtifacts(result, report);

        if (report.VisibleAcceptanceStatus == Phase1VisibleAcceptanceStatus.InsufficientDisplays)
        {
            Assert.Skip($"Two active monitors with different effective DPI are required. Topology retained at '{result.ArtifactDirectory}'.");
        }

        report.VisibleAcceptanceStatus.Should().Be(Phase1VisibleAcceptanceStatus.Passed);
        report.WindowObservations.Select(static observation => observation.Stage).Should().ContainInOrder(
            "WindowsInitialPlacement",
            "SecondaryMonitor",
            "PrimaryMonitor");
        report.WindowObservations.Single(static observation => observation.Stage == "SecondaryMonitor").Dpi.Should().NotBe(
            report.WindowObservations.Single(static observation => observation.Stage == "PrimaryMonitor").Dpi);
        report.FinalResources.TotalActive.Should().Be(0);
    }

    private static void AssertRetainedArtifacts(Phase1TestRunResult result, Phase1TestReport report)
    {
        result.ArtifactsRetained.Should().BeTrue();
        foreach (var relativePath in report.RetainedArtifactPaths)
        {
            Path.IsPathFullyQualified(relativePath).Should().BeFalse();
            File.Exists(Path.Combine(result.ArtifactDirectory, relativePath)).Should().BeTrue();
        }

        File.Exists(Path.Combine(result.ArtifactDirectory, "request.json")).Should().BeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "report.json")).Should().BeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "stdout.txt")).Should().BeTrue();
        File.Exists(Path.Combine(result.ArtifactDirectory, "stderr.txt")).Should().BeTrue();
    }

    private static string GetAssemblyMetadata(string key) => typeof(VisibleDesktopAcceptanceTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
        .Value
        ?? throw new InvalidOperationException($"Assembly metadata '{key}' does not contain a value.");

    private static Phase1TestReport RequireSuccessfulReport(Phase1TestRunResult result)
    {
        result.TimedOut.Should().BeFalse($"artifacts were retained at '{result.ArtifactDirectory}'");
        result.ExitCode.Should().Be(0, $"stderr: {result.StandardError}; artifacts: '{result.ArtifactDirectory}'");
        result.Report.Should().NotBeNull($"artifacts were retained at '{result.ArtifactDirectory}'");
        result.Report!.Succeeded.Should().BeTrue(
            $"failure: {result.Report.ObservedFailure?.Message}; stderr: {result.StandardError}; artifacts: '{result.ArtifactDirectory}'");
        return result.Report;
    }

    private static Task<Phase1TestRunResult> RunAsync(Phase1TestScenario scenario) => Phase1TestProcessRunner.RunAsync(new Phase1TestRunOptions
    {
        TestAppPath = _testAppPath,
        ArtifactRoot = _artifactRoot,
        Scenario = scenario,
        PresentationMode = Phase1TestPresentationMode.Visible,
        RetainArtifactsOnSuccess = true,
        Timeout = TimeSpan.FromMinutes(2),
    });
}
