using System.Reflection;

using AwesomeAssertions;

using Nanto.Hosting.Windows.IntegrationTestKit;
using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.HiddenIntegrationTests;

public sealed class HostIntegrationTests
{
    private static readonly string[] _expectedAcquisitionCheckpoints =
    [
        "ApplicationHostStarted",
        "UiThreadStarted",
        "NativeMessageQueueCreated",
        "DispatcherCreated",
        "WindowClassRegistered",
        "WindowCreated",
    ];

    public static TheoryData<string> AcquisitionCheckpoints => new(_expectedAcquisitionCheckpoints);

    [Fact]
    public async Task HiddenHostLifecycleUsesTheProductionProcessAndReturnsEveryResourceToZero()
    {
        var result = await RunAsync(Phase1TestScenario.HostLifecycle);
        var report = RequireSuccessfulReport(result);

        report.ReachedCheckpoints.Should().Equal(_expectedAcquisitionCheckpoints);
        report.LifecycleTransitions.Select(static transition => (transition.Owner, transition.NewState)).Should().ContainInOrder(
            ("Application", "Creating"),
            ("Application", "Created"),
            ("Application", "Activated"),
            ("Application", "Closing"),
            ("Application", "Closed"));
        report.ObservedFailure.Should().BeNull();
        AssertFinalLedgerIsZero(report);
        AssertReverseOwnershipCleanup(report);
    }

    [Theory]
    [MemberData(nameof(AcquisitionCheckpoints))]
    public async Task EveryImplementedAcquisitionCheckpointFailsAndCleansUpInTheExternalProcess(string checkpoint)
    {
        var result = await RunAsync(Phase1TestScenario.AcquisitionFailure, checkpoint);
        var report = RequireSuccessfulReport(result);

        var checkpointIndex = Array.IndexOf(_expectedAcquisitionCheckpoints, checkpoint);
        report.ReachedCheckpoints.Should().Equal(_expectedAcquisitionCheckpoints.Take(checkpointIndex + 1));
        report.PeakResources.TotalActive.Should().BeGreaterThan(0);
        report.ObservedFailure.Should().NotBeNull();
        report.ObservedFailure!.FailureStage.Should().Be("Startup");
        report.ObservedFailure.PrimaryExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        report.ObservedFailure.PrimaryMessage.Should().Be($"Injected failure after acquiring {checkpoint}.");
        report.ObservedFailure.CleanupFailures.Should().BeEmpty();
        AssertFinalLedgerIsZero(report);
        AssertReverseOwnershipCleanup(report);
    }

    [Theory]
    [InlineData(Phase1TestScenario.NativeClose)]
    [InlineData(Phase1TestScenario.RunCancellation)]
    [InlineData(Phase1TestScenario.RepeatedClose)]
    public async Task AlternateShutdownSignalsConvergeOnOneCleanLifecycle(Phase1TestScenario scenario)
    {
        var result = await RunAsync(scenario);
        var report = RequireSuccessfulReport(result);

        report.ReachedCheckpoints.Should().Equal(_expectedAcquisitionCheckpoints);
        report.ObservedFailure.Should().BeNull();
        report.LifecycleTransitions.Count(transition => transition.Owner == "PrimaryWindow" && transition.NewState == "Closing").Should().Be(1);
        report.LifecycleTransitions.Count(transition => transition.Owner == "PrimaryWindow" && transition.NewState == "Closed").Should().Be(1);
        report.LifecycleTransitions.Count(transition => transition.Owner == "Application" && transition.NewState == "Closed").Should().Be(1);
        AssertFinalLedgerIsZero(report);
        AssertReverseOwnershipCleanup(report);
    }

    [Fact]
    public async Task ScenarioTimeoutTerminatesTheContainedProcessAndRetainsDiagnostics()
    {
        var result = await RunAsync(Phase1TestScenario.ContainmentTimeout, timeout: TimeSpan.FromMilliseconds(250));

        try
        {
            result.Succeeded.Should().BeFalse();
            result.TimedOut.Should().BeTrue();
            result.ExitCode.Should().NotBe(0);
            result.Report.Should().BeNull();
            result.ArtifactsRetained.Should().BeTrue();
            Directory.Exists(result.ArtifactDirectory).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(result.ArtifactDirectory))
            {
                Directory.Delete(result.ArtifactDirectory, recursive: true);
            }
        }
    }

    private static void AssertFinalLedgerIsZero(Phase1TestReport report)
    {
        report.FinalResources.TotalActive.Should().Be(0);
        report.FinalResources.TotalAcquired.Should().Be(report.FinalResources.TotalReleased);
        report.FinalResources.Resources.Should().OnlyContain(static resource => resource.Active == 0);
        report.LifecycleTransitions.Select(static transition => (transition.Owner, transition.NewState)).Should().Contain(
            ("Application", "Closed"));
    }

    private static void AssertReverseOwnershipCleanup(Phase1TestReport report)
    {
        var ownershipEvents = report.ResourceOwnershipEvents
            .Where(static ownershipEvent => ownershipEvent.Kind != "DispatcherItem")
            .ToArray();
        ownershipEvents.Select(static ownershipEvent => ownershipEvent.Sequence).Should().BeInAscendingOrder();

        var acquisitions = ownershipEvents.Where(static ownershipEvent => ownershipEvent.Action == "Acquired").ToArray();
        var releases = ownershipEvents.Where(static ownershipEvent => ownershipEvent.Action == "Released").ToArray();
        acquisitions.Should().NotBeEmpty();
        releases.Select(static ownershipEvent => ownershipEvent.LeaseId).Should().Equal(acquisitions.Reverse().Select(static ownershipEvent => ownershipEvent.LeaseId));
        releases.Select(static ownershipEvent => ownershipEvent.Name).Should().Equal(acquisitions.Reverse().Select(static ownershipEvent => ownershipEvent.Name));
    }

    private static string GetAssemblyMetadata(string name)
    {
        return typeof(HostIntegrationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == name)
            .Value ?? throw new InvalidOperationException($"Assembly metadata '{name}' does not have a value.");
    }

    private static Phase1TestReport RequireSuccessfulReport(Phase1TestRunResult result)
    {
        result.Succeeded.Should().BeTrue("the external TestApp must succeed; stdout: {0}; stderr: {1}; artifacts: {2}",
            result.StandardOutput,
            result.StandardError,
            result.ArtifactDirectory);
        result.ArtifactsRetained.Should().BeFalse();
        result.Report.Should().NotBeNull();
        return result.Report!;
    }

    private static Task<Phase1TestRunResult> RunAsync(
        Phase1TestScenario scenario,
        string? failureCheckpoint = null,
        TimeSpan? timeout = null)
    {
        return Phase1TestProcessRunner.RunAsync(
            new Phase1TestRunOptions
            {
                TestAppPath = GetAssemblyMetadata("NantoTestAppPath"),
                ArtifactRoot = Path.Combine(GetAssemblyMetadata("NantoRepositoryRoot"), "artifacts", "phase1", "runs"),
                Scenario = scenario,
                FailureCheckpoint = failureCheckpoint,
                Timeout = timeout ?? TimeSpan.FromSeconds(45),
            },
            TestContext.Current.CancellationToken);
    }
}
