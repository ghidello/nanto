using System.Diagnostics;

using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.TestApp;

internal static class ScenarioRunner
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
        host = new WindowsApplicationHost(TimeProvider.System, failureInjector);
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
        var reachedCheckpoints = failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray();
        var succeeded = observedFailure is not null
            && ReferenceEquals(observedFailure.InnerException, failureInjector.InjectedFailure)
            && observedFailure.CleanupExceptions.Count == 0
            && disposalFailure is null
            && failureInjector.ReachedCheckpoints.Count > 0
            && failureInjector.ReachedCheckpoints[^1] == failureCheckpoint
            && host.State == ApplicationState.Closed
            && finalResources.TotalActive == 0;
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
            reportedFailure);
    }

    public static async Task<Phase1TestReport> RunHostLifecycleAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var failureInjector = new RecordingFailureInjector(null);
        var transitions = new List<Phase1LifecycleTransition>();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new WindowsApplicationHost(TimeProvider.System, failureInjector);
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
        var run = host.RunAsync(CreateOptions(request));
        try
        {
            await activated.Task.WaitAsync(Program.ActivationTimeout);
            presentationMatched = host.PrimaryWindow?.IsVisible == (request.PresentationMode == Phase1TestPresentationMode.Visible);
            peakResources = CaptureResources(host.ResourceSnapshot);
            await host.StopAsync();
            await run;
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

        var finalResources = CaptureResources(host.ResourceSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            observedFailure is null && presentationMatched && host.State == ApplicationState.Closed && finalResources.TotalActive == 0,
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            observedFailure);
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
        Exception? observedFailure)
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
            RendererRecoveryResult = "NotApplicable",
            RetainedArtifactPaths = [],
            ObservedFailure = observedFailure is null ? null : Program.DescribeFailure(observedFailure),
        };
    }

    private static NantoApplicationOptions CreateOptions(Phase1TestRequest request) => new()
    {
        ApplicationId = request.ApplicationId,
        Assets = UnusedAssetProvider.Instance,
        PrimaryWindow = new WindowOptions
        {
            Title = "Nanto Phase 1 integration host",
            StartVisible = request.PresentationMode == Phase1TestPresentationMode.Visible,
        },
        ShutdownMode = ShutdownMode.OnPrimaryWindowClosed,
        ShutdownTimeout = TimeSpan.FromSeconds(15),
    };

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

    private static Phase1AcquisitionCheckpoint ParseFailureCheckpoint(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: false, out Phase1AcquisitionCheckpoint checkpoint) || !Enum.IsDefined(checkpoint))
        {
            throw new ArgumentException($"Unknown or incorrectly cased acquisition checkpoint '{value}'.", nameof(value));
        }

        return checkpoint;
    }

    private sealed class UnusedAssetProvider : IWebAssetProvider
    {
        public static UnusedAssetProvider Instance { get; } = new();

        private UnusedAssetProvider()
        {
        }

        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Milestone 2 does not acquire web assets.");
        }
    }
}
