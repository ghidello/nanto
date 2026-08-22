using System.Diagnostics;

using Nanto.Hosting.Windows.TestProtocol;
using Nanto.Generated;

using Windows.Win32;

namespace Nanto.Hosting.Windows.TestApp;

internal static partial class ScenarioRunner
{
    public static async Task<Phase1TestReport> RunAcquisitionFailureAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var failureCheckpoint = ParseFailureCheckpoint(request.FailureCheckpoint!);
        WindowsApplicationHost? host = null;
        var failureInjector = new RecordingFailureInjector(failureCheckpoint, () => host!.ResourceSnapshot);
        var transitions = new List<Phase1LifecycleTransition>();
        host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) => transitions.Add(CreateTransition("Application", eventArgs));

        NantoHostException? observedFailure = null;
        try
        {
            await host.RunAsync(CreateOptions(request));
        }
        catch (NantoHostException exception)
        {
            observedFailure = exception;
        }

        var peakResources = CaptureResources(failureInjector.SnapshotAtFailure ?? host.ResourceSnapshot);
        Exception? disposalFailure = null;
        try
        {
            await host.DisposeAsync();
        }
        catch (NantoHostException exception) when (ReferenceEquals(exception, observedFailure))
        {
            // A failed host preserves its failed RunAsync result for DisposeAsync callers; teardown has already completed.
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        var finalResources = CaptureResources(host.ResourceSnapshot);
        var resourceOwnershipEvents = CaptureOwnershipEvents(host.ResourceSnapshot);
        var reachedCheckpoints = failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray();
        var succeeded = observedFailure is not null
            && ReferenceEquals(observedFailure.InnerException, failureInjector.InjectedFailure)
            && observedFailure.CleanupExceptions.Count == 0
            && disposalFailure is null
            && failureInjector.ReachedCheckpoints.Count > 0
            && failureInjector.ReachedCheckpoints[^1] == failureCheckpoint
            && host.State == ApplicationState.Closed
            && finalResources.TotalActive == 0
            && HasDependencyOrderedCleanup(host.ResourceSnapshot);
        Exception? reportedFailure = observedFailure;
        if (disposalFailure is not null)
        {
            reportedFailure = observedFailure is null
                ? disposalFailure
                : new AggregateException("The injected scenario and host disposal both failed.", observedFailure, disposalFailure);
        }

        return CreateReport(
            request,
            startedAt,
            stopwatch,
            succeeded,
            reachedCheckpoints,
            transitions,
            initialResources,
            peakResources,
            finalResources,
            resourceOwnershipEvents,
            reportedFailure);
    }

    public static async Task<Phase1TestReport> RunHostLifecycleAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(request, startedAt, stopwatch, ShutdownAction.Stop);

    public static async Task<Phase1TestReport> RunBridgeUnaryAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(
            request,
            startedAt,
            stopwatch,
            ShutdownAction.Stop,
            "/bridge.html",
            VerifyBridgeUnaryAsync);

    public static async Task<Phase1TestReport> RunBridgeNavigationAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(
            request,
            startedAt,
            stopwatch,
            ShutdownAction.Stop,
            "/bridge-navigation.html",
            VerifyBridgeNavigationAsync);

    public static async Task<Phase1TestReport> RunBridgeCloseAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var projectsApi = new ProjectsApi(1);
        return await RunOrderlyShutdownAsync(
            request,
            startedAt,
            stopwatch,
            ShutdownAction.Stop,
            "/bridge-close.html",
            VerifyBridgeCloseStartedAsync,
            projectsApi,
            () => VerifyBridgeCloseCancellationAsync(projectsApi));
    }

    public static async Task<Phase1TestReport> RunBridgeSecurityAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(
            request,
            startedAt,
            stopwatch,
            ShutdownAction.Stop,
            "/bridge-security.html",
            VerifyBridgeSecurityAsync,
            grantOpen: false);

    public static async Task<Phase1TestReport> RunStartupCancellationAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var cancellationCheckpoint = ParseFailureCheckpoint(request.FailureCheckpoint!);
        using var cancellation = new CancellationTokenSource();
        WindowsApplicationHost? host = null;
        var failureInjector = new RecordingFailureInjector(
            null,
            () => host!.ResourceSnapshot,
            checkpoint =>
            {
                if (checkpoint == cancellationCheckpoint)
                {
                    cancellation.Cancel();
                }
            });
        var transitions = new List<Phase1LifecycleTransition>();
        host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) => transitions.Add(CreateTransition("Application", eventArgs));
        Exception? observedFailure = null;
        try
        {
            await host.RunAsync(CreateOptions(request), cancellation.Token);
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception) when (ReferenceEquals(exception, observedFailure))
        {
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is not null
                && failureInjector.ReachedCheckpoints[^1] == cancellationCheckpoint
                && host.State == ApplicationState.Closed
                && finalResources.TotalActive == 0
                && HasDependencyOrderedCleanup(finalSnapshot),
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            CaptureResources(finalSnapshot),
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure);
    }

    public static async Task<Phase1TestReport> RunNativeCloseAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(request, startedAt, stopwatch, ShutdownAction.NativeClose);

    public static async Task<Phase1TestReport> RunCancellationAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(request, startedAt, stopwatch, ShutdownAction.CancelRun);

    public static async Task<Phase1TestReport> RunRepeatedCloseAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(request, startedAt, stopwatch, ShutdownAction.RepeatedClose);

    public static async Task<Phase1TestReport> RunNavigationAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch) => await RunOrderlyShutdownAsync(
            request,
            startedAt,
            stopwatch,
            ShutdownAction.Stop,
            "/navigation.html?step=initial");

    public static async Task<Phase1TestReport> RunAppearanceAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var failureInjector = new RecordingFailureInjector(null);
        var transitions = new List<Phase1LifecycleTransition>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) =>
        {
            transitions.Add(CreateTransition("Application", eventArgs));
            if (eventArgs.NewState == ApplicationState.Activated)
            {
                activated.TrySetResult();
            }
        };
        Exception? observedFailure = null;
        var observations = new List<string>();
        var peakResources = initialResources;
        var run = host.RunAsync(CreateOptions(request) with { PreferredColorScheme = ColorSchemePreference.Dark });
        try
        {
            await activated.Task.WaitAsync(Program.ActivationTimeout);
            await WaitForReadinessAsync(host);
            await WaitForAppearanceAsync(host, "Dark", observations);
            await host.SetPreferredColorSchemeAsync(ColorSchemePreference.Light);
            await WaitForAppearanceAsync(host, "Light", observations);
            await host.SetPreferredColorSchemeAsync(ColorSchemePreference.System);
            observations.Add($"System:{host.PreferredColorScheme}");
            peakResources = CaptureResources(host.ResourceSnapshot);
            await host.StopAsync();
            await run;
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            observedFailure = observedFailure is null ? exception : new AggregateException(observedFailure, exception);
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is null && observations.SequenceEqual(["Dark", "Light", "System:System"]) && finalResources.TotalActive == 0,
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure,
            [.. observations]);
    }

    public static async Task<Phase1TestReport> RunRendererRecoveryAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var failureInjector = new RecordingFailureInjector(null);
        var transitions = new List<Phase1LifecycleTransition>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendererFailure = new TaskCompletionSource<RendererFailedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) =>
        {
            transitions.Add(CreateTransition("Application", eventArgs));
            if (eventArgs.NewState == ApplicationState.Activated)
            {
                activated.TrySetResult();
            }
        };
        Exception? observedFailure = null;
        var peakResources = initialResources;
        var rendererRecoveryResult = "NotObserved";
        var run = host.RunAsync(CreateOptions(request) with { PreferredColorScheme = ColorSchemePreference.Dark });
        try
        {
            await activated.Task.WaitAsync(Program.ActivationTimeout);
            var window = host.PrimaryWindow ?? throw new InvalidOperationException("The primary window was not published.");
            window.StateChanged += (_, eventArgs) => transitions.Add(CreateTransition("PrimaryWindow", eventArgs));
            window.RendererFailed += (_, eventArgs) => rendererFailure.TrySetResult(eventArgs);
            await WaitForReadinessAsync(host);
            var observations = new List<string>();
            await WaitForAppearanceAsync(host, "Dark", observations);
            await host.CrashRendererForTestingAsync();
            var failure = await rendererFailure.Task.WaitAsync(Program.ActivationTimeout);
            await WaitForAppearanceAsync(host, "Dark", observations);
            rendererRecoveryResult = $"{failure.Kind}:{failure.WillAttemptRecovery}:Reloaded";
            peakResources = CaptureResources(host.ResourceSnapshot);
            await host.StopAsync();
            await run;
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            observedFailure = observedFailure is null ? exception : new AggregateException(observedFailure, exception);
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is null && rendererRecoveryResult == "Exited:True:Reloaded" && finalResources.TotalActive == 0,
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure,
            rendererRecoveryResult: rendererRecoveryResult);
    }

    public static async Task<Phase1TestReport> RunBrowserProcessExitAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var failureInjector = new RecordingFailureInjector(null);
        var transitions = new List<Phase1LifecycleTransition>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendererFailure = new TaskCompletionSource<RendererFailedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) =>
        {
            transitions.Add(CreateTransition("Application", eventArgs));
            if (eventArgs.NewState == ApplicationState.Activated)
            {
                activated.TrySetResult();
            }
        };
        Exception? observedFailure = null;
        var peakResources = initialResources;
        var rendererRecoveryResult = "NotObserved";
        var run = host.RunAsync(CreateOptions(request));
        try
        {
            await activated.Task.WaitAsync(Program.ActivationTimeout);
            var window = host.PrimaryWindow ?? throw new InvalidOperationException("The primary window was not published.");
            window.StateChanged += (_, eventArgs) => transitions.Add(CreateTransition("PrimaryWindow", eventArgs));
            window.RendererFailed += (_, eventArgs) => rendererFailure.TrySetResult(eventArgs);
            await WaitForReadinessAsync(host);
            var browserProcessId = await host.GetBrowserProcessIdForTestingAsync();
            using (var browserProcess = Process.GetProcessById(checked((int)browserProcessId)))
            {
                browserProcess.Kill();
            }

            var failure = await rendererFailure.Task.WaitAsync(Program.ActivationTimeout);
            await run.WaitAsync(Program.ActivationTimeout);
            rendererRecoveryResult = $"{failure.Kind}:{failure.WillAttemptRecovery}:{window.State}";
            peakResources = CaptureResources(host.ResourceSnapshot);
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            observedFailure = observedFailure is null ? exception : new AggregateException(observedFailure, exception);
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is null && rendererRecoveryResult == "Exited:False:Closed" && finalResources.TotalActive == 0,
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure,
            rendererRecoveryResult: rendererRecoveryResult);
    }

    public static async Task<Phase1TestReport> RunSharedProfileAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var failureInjector = new RecordingFailureInjector(null);
        var transitions = new List<Phase1LifecycleTransition>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) =>
        {
            transitions.Add(CreateTransition("Application", eventArgs));
            if (eventArgs.NewState == ApplicationState.Activated)
            {
                activated.TrySetResult();
            }
        };

        Exception? observedFailure = null;
        var observations = new List<string>();
        var peakResources = initialResources;
        var run = host.RunAsync(CreateOptions(request) with { PreferredColorScheme = ColorSchemePreference.Dark });
        try
        {
            await activated.Task.WaitAsync(Program.ActivationTimeout);
            await WaitForReadinessAsync(host);
            await WaitForAppearanceAsync(host, "Dark", observations);
            var coordinationDirectory = request.CoordinationDirectory!;
            var participantId = request.ParticipantId!;
            File.WriteAllText(Path.Combine(coordinationDirectory, $"{participantId}.ready"), string.Empty);
            if (participantId == "first")
            {
                await WaitForMarkerAsync(coordinationDirectory, "release-first");
            }
            else
            {
                await WaitForMarkerAsync(coordinationDirectory, "probe-second");
                await host.SetPreferredColorSchemeAsync(ColorSchemePreference.Light);
                await WaitForAppearanceAsync(host, "Light", observations);
                File.WriteAllText(Path.Combine(coordinationDirectory, "second.probed"), string.Empty);
                await WaitForMarkerAsync(coordinationDirectory, "release-second");
            }

            peakResources = CaptureResources(host.ResourceSnapshot);
            await host.StopAsync();
            await run;
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            observedFailure = observedFailure is null ? exception : new AggregateException(observedFailure, exception);
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        string[] expectedObservations = request.ParticipantId == "second" ? ["Dark", "Light"] : ["Dark"];
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is null
                && observations.SequenceEqual(expectedObservations)
                && host.State == ApplicationState.Closed
                && finalResources.TotalActive == 0
                && HasDependencyOrderedCleanup(finalSnapshot),
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure,
            [.. observations]);
    }

    private static Phase1ResourceOwnershipEvent[] CaptureOwnershipEvents(ResourceLedgerSnapshot snapshot)
    {
        return snapshot.Events.Select(static ownershipEvent => new Phase1ResourceOwnershipEvent
        {
            Sequence = ownershipEvent.Sequence,
            LeaseId = ownershipEvent.LeaseId,
            Kind = ownershipEvent.Kind.ToString(),
            Name = ownershipEvent.Name,
            Action = ownershipEvent.Acquired ? "Acquired" : "Released",
        }).ToArray();
    }

    private static Phase1ResourceLedgerReport CaptureResources(ResourceLedgerSnapshot snapshot) => new()
    {
        Resources = Enum.GetValues<WindowsResourceKind>()
            .Select(kind => new Phase1ResourceCount
            {
                Name = kind.ToString(),
                Active = snapshot.GetActiveCount(kind),
                Peak = snapshot.GetPeakCount(kind),
            })
            .ToArray(),
        TotalAcquired = snapshot.TotalAcquired,
        TotalReleased = snapshot.TotalReleased,
        TotalActive = snapshot.TotalActive,
    };

    private static Phase1TestReport CreateReport(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        bool succeeded,
        string[] reachedCheckpoints,
        List<Phase1LifecycleTransition> transitions,
        Phase1ResourceLedgerReport initialResources,
        Phase1ResourceLedgerReport peakResources,
        Phase1ResourceLedgerReport finalResources,
        Phase1ResourceOwnershipEvent[] resourceOwnershipEvents,
        Exception? observedFailure,
        string[]? appearanceObservations = null,
        string rendererRecoveryResult = "NotApplicable",
        Phase1VisibleAcceptanceStatus visibleAcceptanceStatus = Phase1VisibleAcceptanceStatus.NotApplicable,
        Phase1MonitorObservation[]? monitorTopology = null,
        Phase1WindowObservation[]? windowObservations = null,
        string[]? retainedArtifactPaths = null)
    {
        stopwatch.Stop();
        return new Phase1TestReport
        {
            ProtocolVersion = Phase1TestProtocol.CurrentVersion,
            Scenario = request.Scenario.ToString(),
            Succeeded = succeeded,
            HostEnvironment = new Phase1HostEnvironment
            {
                FrameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OperatingSystemDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeVersion = Environment.Version.ToString(),
            },
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            ReachedCheckpoints = reachedCheckpoints,
            LifecycleTransitions = [.. transitions],
            InitialResources = initialResources,
            PeakResources = peakResources,
            FinalResources = finalResources,
            ResourceOwnershipEvents = resourceOwnershipEvents,
            RendererRecoveryResult = rendererRecoveryResult,
            AppearanceObservations = appearanceObservations ?? [],
            VisibleAcceptanceStatus = visibleAcceptanceStatus,
            MonitorTopology = monitorTopology ?? [],
            WindowObservations = windowObservations ?? [],
            RetainedArtifactPaths = retainedArtifactPaths ?? [],
            ObservedFailure = observedFailure is null ? null : Program.DescribeFailure(observedFailure),
        };
    }

    private static NantoApplicationOptions CreateOptions(Phase1TestRequest request, ProjectsApi? projectsApi = null, bool grantOpen = true)
    {
        var bridge = new NantoBridgeConfiguration();
        bridge.Add(projectsApi ?? new ProjectsApi(1));
        bridge.Add(new ProjectBuildsApi(100));
        return new NantoApplicationOptions
        {
            ApplicationId = request.ApplicationId,
            Content = new NantoProductionContent
            {
                Assets = VersionedWebAssetProvider.FromAssembly<TestAppAssetMarker>(
                    "Nanto.Hosting.Windows.TestApp.WebAssets.nanto-assets.json"),
                InitialRoute = "/index.html",
            },
            Bridge = bridge,
            PrimaryWindow = new WindowOptions
            {
                Capabilities = grantOpen
                    ?
                    [
                        AppCapabilities.Projects.Open,
                        AppCapabilities.Projects.Build,
                        AppCapabilities.Projects.Changed,
                        AppCapabilities.Projects.Change,
                        AppCapabilities.Projects.GetActiveWaitCount,
                        AppCapabilities.Projects.GetCancellationCount,
                        AppCapabilities.Projects.Wait,
                    ]
                    :
                    [
                        AppCapabilities.Projects.Build,
                        AppCapabilities.Projects.Changed,
                        AppCapabilities.Projects.Change,
                        AppCapabilities.Projects.GetActiveWaitCount,
                        AppCapabilities.Projects.GetCancellationCount,
                        AppCapabilities.Projects.Wait,
                    ],
                Title = "Nanto Phase 1 integration host",
                StartVisible = request.PresentationMode == Phase1TestPresentationMode.Visible,
            },
            ShutdownMode = ShutdownMode.OnPrimaryWindowClosed,
            ShutdownTimeout = TimeSpan.FromSeconds(15),
        };
    }

    private static async Task WaitForAppearanceAsync(WindowsApplicationHost host, string expected, List<string> observations)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        while (true)
        {
            var message = await host.WaitForDiagnosticMessageAsync(timeout.Token);
            const string prefix = "nanto:test:appearance:";
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                var observation = message[prefix.Length..];
                observations.Add(observation);
                if (!string.Equals(observation, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Expected the SPA to observe '{expected}', but it observed '{observation}'.");
                }

                return;
            }
        }
    }

    private static Phase1LifecycleTransition CreateTransition(string owner, ApplicationStateChangedEventArgs eventArgs) => new()
    {
        Owner = owner,
        OldState = eventArgs.OldState.ToString(),
        NewState = eventArgs.NewState.ToString(),
        OccurredAt = eventArgs.OccurredAt,
    };

    private static Phase1LifecycleTransition CreateTransition(string owner, WindowStateChangedEventArgs eventArgs) => new()
    {
        Owner = owner,
        OldState = eventArgs.OldState.ToString(),
        NewState = eventArgs.NewState.ToString(),
        OccurredAt = eventArgs.OccurredAt,
    };

    private static bool HasDependencyOrderedCleanup(ResourceLedgerSnapshot snapshot)
    {
        var ownershipEvents = snapshot.Events.Where(static ownershipEvent => ownershipEvent.Kind != WindowsResourceKind.DispatcherItem).ToArray();
        var acquiredLeaseIds = ownershipEvents.Where(static ownershipEvent => ownershipEvent.Acquired).Select(static ownershipEvent => ownershipEvent.LeaseId).ToHashSet();
        var releases = ownershipEvents.Where(static ownershipEvent => !ownershipEvent.Acquired).ToArray();
        if (acquiredLeaseIds.Count == 0 || !acquiredLeaseIds.SetEquals(releases.Select(static ownershipEvent => ownershipEvent.LeaseId)))
        {
            return false;
        }

        var expectedOrder = new[]
        {
            "WebMessageReceivedSubscription",
            "NavigationCompletedSubscription",
            "NavigationStartingSubscription",
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
        var knownReleases = releases.Select(static ownershipEvent => ownershipEvent.Name).Where(expectedOrder.Contains).ToArray();
        return knownReleases.SequenceEqual(expectedOrder.Where(knownReleases.Contains));
    }

    private static Phase1AcquisitionCheckpoint ParseFailureCheckpoint(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: false, out Phase1AcquisitionCheckpoint checkpoint) || !Enum.IsDefined(checkpoint))
        {
            throw new ArgumentException($"Unknown or incorrectly cased acquisition checkpoint '{value}'.", nameof(value));
        }

        return checkpoint;
    }

    private static async Task<Phase1TestReport> RunOrderlyShutdownAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        ShutdownAction shutdownAction,
        string initialRoute = "/index.html",
        Func<WindowsApplicationHost, Task>? verify = null,
        ProjectsApi? projectsApi = null,
        Func<Task>? verifyAfterShutdown = null,
        bool grantOpen = true)
    {
        var failureInjector = new RecordingFailureInjector(null);
        var transitions = new List<Phase1LifecycleTransition>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runCancellation = new CancellationTokenSource();
        var host = new WindowsApplicationHost(TimeProvider.System, failureInjector, captureResourceOwnershipEvents: true);
        var initialResources = CaptureResources(host.ResourceSnapshot);
        host.StateChanged += (_, eventArgs) =>
        {
            transitions.Add(CreateTransition("Application", eventArgs));
            if (eventArgs.NewState == ApplicationState.Created && host.PrimaryWindow is { } window)
            {
                window.StateChanged += (_, windowEventArgs) => transitions.Add(CreateTransition("PrimaryWindow", windowEventArgs));
            }

            if (eventArgs.NewState == ApplicationState.Activated)
            {
                activated.TrySetResult();
            }
        };

        Exception? observedFailure = null;
        var presentationMatched = false;
        var peakResources = initialResources;
        var options = CreateOptions(request, projectsApi, grantOpen);
        var run = host.RunAsync(
            options with { Content = ((NantoProductionContent)options.Content) with { InitialRoute = initialRoute } },
            runCancellation.Token);
        try
        {
            var activation = activated.Task.WaitAsync(Program.ActivationTimeout);
            if (await Task.WhenAny(activation, run) == run)
            {
                await run;
            }

            await activation;
            await WaitForReadinessAsync(host);
            if (verify is not null)
            {
                await verify(host);
            }

            presentationMatched = host.PrimaryWindow?.IsVisible == (request.PresentationMode == Phase1TestPresentationMode.Visible);
            peakResources = CaptureResources(host.ResourceSnapshot);
            await RequestShutdownAsync(host, runCancellation, shutdownAction);
            await run;
            if (verifyAfterShutdown is not null)
            {
                await verifyAfterShutdown();
            }
        }
        catch (Exception exception)
        {
            observedFailure = exception;
            try
            {
                await host.StopAsync();
            }
            catch (Exception stopException)
            {
                observedFailure = new AggregateException("The scenario and its stop request both failed.", exception, stopException);
            }
        }

        try
        {
            await host.DisposeAsync();
        }
        catch (Exception disposeException)
        {
            observedFailure = observedFailure is null
                ? disposeException
                : new AggregateException("The scenario and host disposal both failed.", observedFailure, disposeException);
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is null
                && presentationMatched
                && host.State == ApplicationState.Closed
                && finalResources.TotalActive == 0
                && HasDependencyOrderedCleanup(finalSnapshot),
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure);
    }

    private static async Task VerifyBridgeUnaryAsync(WindowsApplicationHost host)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        var message = await host.WaitForDiagnosticMessageAsync(timeout.Token);
        if (!string.Equals(message, "nanto:test:bridge:unary:passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The bridge fixture returned an unexpected result: '{message}'.");
        }
    }

    private static async Task VerifyBridgeNavigationAsync(WindowsApplicationHost host)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        var message = await host.WaitForDiagnosticMessageAsync(timeout.Token);
        if (!string.Equals(message, "nanto:test:bridge:navigation:passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The bridge navigation fixture returned an unexpected result: '{message}'.");
        }
    }

    private static async Task VerifyBridgeCloseStartedAsync(WindowsApplicationHost host)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        var message = await host.WaitForDiagnosticMessageAsync(timeout.Token);
        if (!string.Equals(message, "nanto:test:bridge:close:started", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The bridge close fixture returned an unexpected result: '{message}'.");
        }
    }

    private static async Task VerifyBridgeCloseCancellationAsync(ProjectsApi projectsApi)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (projectsApi.CancellationCount != 1 || projectsApi.ActiveWaitCount != 0)
        {
            if (Stopwatch.GetElapsedTime(startedAt) >= Program.ActivationTimeout)
            {
                throw new TimeoutException("Closing the bridge did not cancel its active native invocation.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task VerifyBridgeSecurityAsync(WindowsApplicationHost host)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        var message = await host.WaitForDiagnosticMessageAsync(timeout.Token);
        if (!string.Equals(message, "nanto:test:bridge:security:passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The bridge security fixture returned an unexpected result: '{message}'.");
        }
    }

    private static async Task RequestShutdownAsync(
        WindowsApplicationHost host,
        CancellationTokenSource runCancellation,
        ShutdownAction shutdownAction)
    {
        var window = (WindowsWindow?)host.PrimaryWindow ?? throw new InvalidOperationException("The primary Windows window was not published.");
        switch (shutdownAction)
        {
            case ShutdownAction.Stop:
                await host.StopAsync();
                break;
            case ShutdownAction.NativeClose:
                if (!PInvoke.PostMessage(window.Handle, PInvoke.WM_CLOSE, default, default))
                {
                    throw new InvalidOperationException("Posting WM_CLOSE to the primary window failed.");
                }

                break;
            case ShutdownAction.CancelRun:
                runCancellation.Cancel();
                break;
            case ShutdownAction.RepeatedClose:
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => window.CloseAsync().AsTask()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shutdownAction), shutdownAction, "The shutdown action is not supported.");
        }
    }

    private static async Task WaitForReadinessAsync(WindowsApplicationHost host)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        await host.WaitForWebViewReadinessAsync(timeout.Token);
    }

    private static async Task WaitForMarkerAsync(string coordinationDirectory, string markerName)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        var markerPath = Path.Combine(coordinationDirectory, markerName);
        while (!File.Exists(markerPath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private enum ShutdownAction
    {
        Stop,
        NativeClose,
        CancelRun,
        RepeatedClose,
    }
}

internal sealed class TestAppAssetMarker;
