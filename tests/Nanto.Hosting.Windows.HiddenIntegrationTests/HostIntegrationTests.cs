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
        "AssetLeasePrepared",
        "WebViewEnvironmentCreated",
        "WindowClassRegistered",
        "WindowCreated",
        "WebViewControllerCreated",
        "WebViewCreated",
        "WebViewProfileCreated",
        "WebViewSettingsCreated",
        "WebViewSettingsConfigured",
        "NavigationCompletedSubscriptionAdded",
        "WebMessageSubscriptionAdded",
        "VirtualHostMappingAdded",
        "InitialNavigationCompleted",
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
    [MemberData(nameof(AcquisitionCheckpoints))]
    public async Task CancellationAfterEveryStartupAcquisitionStopsFurtherWorkAndCleansUp(string checkpoint)
    {
        var result = await RunAsync(Phase1TestScenario.StartupCancellation, checkpoint);
        var report = RequireSuccessfulReport(result);

        var checkpointIndex = Array.IndexOf(_expectedAcquisitionCheckpoints, checkpoint);
        report.ReachedCheckpoints.Should().Equal(_expectedAcquisitionCheckpoints.Take(checkpointIndex + 1));
        report.ObservedFailure.Should().NotBeNull();
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
    public async Task ProfileAppearanceFlowsThroughPrefersColorSchemeAndCanChangeLive()
    {
        var result = await RunAsync(Phase1TestScenario.Appearance);
        var report = RequireSuccessfulReport(result);

        report.AppearanceObservations.Should().Equal("Dark", "Light", "System:System");
        report.ObservedFailure.Should().BeNull();
        AssertFinalLedgerIsZero(report);
    }

    [Fact]
    public async Task TwoHostsCanShareTheProductionProfileAndTheSecondRemainsUsableAfterTheFirstCloses()
    {
        var sharedRunId = Guid.NewGuid().ToString("N");
        var applicationId = $"com.nanto.phase1.shared.{sharedRunId}";
        var coordinationDirectory = Path.Combine(
            GetAssemblyMetadata("NantoRepositoryRoot"),
            "artifacts",
            "phase1",
            "shared",
            sharedRunId);
        Directory.CreateDirectory(coordinationDirectory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var processGroup = Phase1TestProcessGroup.Create();
        var first = RunSharedParticipantAsync(applicationId, coordinationDirectory, "first", processGroup, cancellation.Token);
        var second = RunSharedParticipantAsync(applicationId, coordinationDirectory, "second", processGroup, cancellation.Token);
        Phase1TestRunResult? firstResult = null;
        Phase1TestRunResult? secondResult = null;
        try
        {
            await WaitForMarkerAsync(coordinationDirectory, "first.ready", cancellation.Token);
            await WaitForMarkerAsync(coordinationDirectory, "second.ready", cancellation.Token);

            File.WriteAllText(Path.Combine(coordinationDirectory, "release-first"), string.Empty);
            firstResult = await first;
            var firstReport = RequireSuccessfulReport(firstResult);
            AssertFinalLedgerIsZero(firstReport);

            second.IsCompleted.Should().BeFalse("the second host must remain alive after the first host and its browser job exit");
            File.WriteAllText(Path.Combine(coordinationDirectory, "probe-second"), string.Empty);
            await WaitForMarkerAsync(coordinationDirectory, "second.probed", cancellation.Token);
            File.WriteAllText(Path.Combine(coordinationDirectory, "release-second"), string.Empty);
            secondResult = await second;
            var secondReport = RequireSuccessfulReport(secondResult);
            secondReport.AppearanceObservations.Should().Equal("Dark", "Light");
            AssertFinalLedgerIsZero(secondReport);

            firstResult.ApplicationRoot.Should().Be(secondResult.ApplicationRoot);
            Directory.Exists(firstResult.ApplicationRoot).Should().BeTrue();
            processGroup.Dispose();
            await Phase1TestProcessRunner.DeleteApplicationRootAsync(firstResult.ApplicationRoot, cancellation.Token);
            Directory.Exists(firstResult.ApplicationRoot).Should().BeFalse("both WebView2 process trees must release the shared UDF");
        }
        finally
        {
            File.WriteAllText(Path.Combine(coordinationDirectory, "release-first"), string.Empty);
            File.WriteAllText(Path.Combine(coordinationDirectory, "probe-second"), string.Empty);
            File.WriteAllText(Path.Combine(coordinationDirectory, "release-second"), string.Empty);
            cancellation.Cancel();
            await ObserveParticipantAsync(first);
            await ObserveParticipantAsync(second);
            if (Directory.Exists(coordinationDirectory))
            {
                Directory.Delete(coordinationDirectory, recursive: true);
            }
        }
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
        releases.Select(static ownershipEvent => ownershipEvent.LeaseId).Should().BeEquivalentTo(acquisitions.Select(static ownershipEvent => ownershipEvent.LeaseId));

        var expectedOrder = new[]
        {
            "WebMessageReceivedSubscription",
            "NavigationCompletedSubscription",
            "ApplicationOriginMapping",
            "WebView2Settings",
            "WebView2Profile",
            "CoreWebView2",
            "WebView2Controller",
            "WindowHandle",
            "Window",
            "WindowClassRegistration",
            "WebAssetLease",
            "WebView2Environment",
            "UiThread",
            "ApplicationHost",
        };
        var releaseNames = releases.Select(static ownershipEvent => ownershipEvent.Name).Where(expectedOrder.Contains).ToArray();
        releaseNames.Should().Equal(expectedOrder.Where(releaseNames.Contains));
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

    private static Task<Phase1TestRunResult> RunSharedParticipantAsync(
        string applicationId,
        string coordinationDirectory,
        string participantId,
        Phase1TestProcessGroup processGroup,
        CancellationToken cancellationToken)
    {
        return Phase1TestProcessRunner.RunAsync(
            new Phase1TestRunOptions
            {
                TestAppPath = GetAssemblyMetadata("NantoTestAppPath"),
                ArtifactRoot = Path.Combine(GetAssemblyMetadata("NantoRepositoryRoot"), "artifacts", "phase1", "runs"),
                Scenario = Phase1TestScenario.SharedProfile,
                ApplicationId = applicationId,
                CleanupApplicationRootOnSuccess = false,
                CoordinationDirectory = coordinationDirectory,
                ParticipantId = participantId,
                ProcessGroup = processGroup,
                Timeout = TimeSpan.FromSeconds(45),
            },
            cancellationToken);
    }

    private static async Task WaitForMarkerAsync(string coordinationDirectory, string markerName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var markerPath = Path.Combine(coordinationDirectory, markerName);
        while (!File.Exists(markerPath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task ObserveParticipantAsync(Task<Phase1TestRunResult> participant)
    {
        try
        {
            await participant;
        }
        catch
        {
        }
    }
}
