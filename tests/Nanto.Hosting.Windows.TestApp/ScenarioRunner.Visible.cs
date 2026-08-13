using System.Diagnostics;
using System.Text.Json;

using Nanto.Hosting.Windows.TestProtocol;
using Nanto.Hosting.Windows.TestApp.Interop;

namespace Nanto.Hosting.Windows.TestApp;

internal static partial class ScenarioRunner
{
    private static readonly WindowSize _visibleTestSize = new(900, 600);

    public static async Task<Phase1TestReport> RunVisibleDesktopAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var topology = VisibleDesktopAutomation.CaptureTopology();
        var retainedArtifacts = await WriteTopologyAsync(request, topology);
        var observations = new List<Phase1WindowObservation>();
        var appearanceObservations = new List<string>();
        return await RunVisibleHostAsync(
            request,
            startedAt,
            stopwatch,
            topology,
            observations,
            appearanceObservations,
            retainedArtifacts,
            async (host, window) =>
            {
                await EnsureForegroundFocusAsync(host, window);
                await WaitForForegroundFocusAsync(host, window);
                var systemAppearance = await WaitForAppearanceValueAsync(host);
                appearanceObservations.Add($"System:{systemAppearance}");
                await CaptureVisibleObservationAsync(host, window, request, "System", "system.bmp", observations, retainedArtifacts);
                RequireFrameMatchesAppearance(observations[^1], systemAppearance);

                await window.SetSizeAsync(_visibleTestSize);
                await WaitForSizeAsync(window, _visibleTestSize);
                await CaptureVisibleObservationAsync(host, window, request, "Resized", "resized.bmp", observations, retainedArtifacts);

                await host.SetPreferredColorSchemeAsync(ColorSchemePreference.Dark);
                await QueryAppearanceWithF6Async(host, window, "Dark", appearanceObservations);
                await CaptureVisibleObservationAsync(host, window, request, "Dark", "dark.bmp", observations, retainedArtifacts);
                RequireFrameMatchesAppearance(observations[^1], "Dark");

                await host.SetPreferredColorSchemeAsync(ColorSchemePreference.Light);
                await QueryAppearanceWithF6Async(host, window, "Light", appearanceObservations);
                await CaptureVisibleObservationAsync(host, window, request, "Light", "light.bmp", observations, retainedArtifacts);
                RequireFrameMatchesAppearance(observations[^1], "Light");

                await host.SetPreferredColorSchemeAsync(ColorSchemePreference.System);
                if (host.PreferredColorScheme != ColorSchemePreference.System)
                {
                    throw new InvalidOperationException("The visible test host did not accept the System appearance preference.");
                }

                var restoredSystemAppearance = await QueryAppearanceWithF6Async(
                    host,
                    window,
                    systemAppearance,
                    appearanceObservations: appearanceObservations);
                appearanceObservations[^1] = $"SystemRestored:{restoredSystemAppearance}";
                await CaptureVisibleObservationAsync(host, window, request, "SystemRestored", null, observations, retainedArtifacts);
                RequireFrameMatchesAppearance(observations[^1], restoredSystemAppearance);
            });
    }

    public static async Task<Phase1TestReport> RunVisibleCrossMonitorDpiAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        var topology = VisibleDesktopAutomation.CaptureTopology();
        var retainedArtifacts = await WriteTopologyAsync(request, topology);
        var primary = topology.SingleOrDefault(static monitor => monitor.Observation.IsPrimary);
        var secondary = primary is null
            ? null
            : topology.FirstOrDefault(monitor => !monitor.Observation.IsPrimary
                && monitor.Observation.EffectiveDpi != primary.Observation.EffectiveDpi);
        if (primary is null || secondary is null)
        {
            await WriteObservationsAsync(request, [], retainedArtifacts);
            var emptyResources = CreateEmptyResourceReport();
            return CreateReport(
                request,
                startedAt,
                stopwatch,
                succeeded: true,
                [],
                [],
                emptyResources,
                emptyResources,
                emptyResources,
                [],
                observedFailure: null,
                visibleAcceptanceStatus: Phase1VisibleAcceptanceStatus.InsufficientDisplays,
                monitorTopology: [.. topology.Select(static monitor => monitor.Observation)],
                retainedArtifactPaths: [.. retainedArtifacts]);
        }

        var observations = new List<Phase1WindowObservation>();
        return await RunVisibleHostAsync(
            request,
            startedAt,
            stopwatch,
            topology,
            observations,
            [],
            retainedArtifacts,
            async (host, window) =>
            {
                await EnsureForegroundFocusAsync(host, window);
                await WaitForForegroundFocusAsync(host, window);
                await CaptureVisibleObservationAsync(host, window, request, "WindowsInitialPlacement", null, observations, retainedArtifacts);
                if (!topology.Any(monitor => Intersects(observations[^1], monitor.Observation)))
                {
                    throw new InvalidOperationException("Windows placed the visible window outside every active monitor work area.");
                }

                await MoveAndObserveAsync(host, window, secondary.Observation, request, "SecondaryMonitor", "secondary-monitor.bmp", observations, retainedArtifacts);
                await MoveAndObserveAsync(host, window, primary.Observation, request, "PrimaryMonitor", "primary-monitor.bmp", observations, retainedArtifacts);
            });
    }

    private static async Task<Phase1TestReport> RunVisibleHostAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        VisibleDesktopAutomation.VisibleMonitor[] topology,
        List<Phase1WindowObservation> observations,
        List<string> appearanceObservations,
        List<string> retainedArtifacts,
        Func<WindowsApplicationHost, INantoWindow, Task> exercise)
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
        var peakResources = initialResources;
        var run = host.RunAsync(CreateOptions(request));
        try
        {
            await activated.Task.WaitAsync(Program.ActivationTimeout);
            await WaitForReadinessAsync(host);
            var window = host.PrimaryWindow ?? throw new InvalidOperationException("The visible primary window was not published.");
            await exercise(host, window);
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
                observedFailure = new AggregateException("The visible scenario and its stop request both failed.", exception, stopException);
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
                : new AggregateException("The visible scenario and host disposal both failed.", observedFailure, disposeException);
        }

        var finalSnapshot = host.ResourceSnapshot;
        var finalResources = CaptureResources(finalSnapshot);
        await WriteObservationsAsync(request, observations, retainedArtifacts);
        var succeeded = observedFailure is null
            && host.State == ApplicationState.Closed
            && finalResources.TotalActive == 0
            && HasDependencyOrderedCleanup(finalSnapshot);
        return CreateReport(
            request,
            startedAt,
            stopwatch,
            succeeded,
            failureInjector.ReachedCheckpoints.Select(static checkpoint => checkpoint.ToString()).ToArray(),
            transitions,
            initialResources,
            peakResources,
            finalResources,
            CaptureOwnershipEvents(finalSnapshot),
            observedFailure,
            [.. appearanceObservations],
            visibleAcceptanceStatus: succeeded ? Phase1VisibleAcceptanceStatus.Passed : Phase1VisibleAcceptanceStatus.NotApplicable,
            monitorTopology: [.. topology.Select(static monitor => monitor.Observation)],
            windowObservations: [.. observations],
            retainedArtifactPaths: [.. retainedArtifacts]);
    }

    private static async Task CaptureVisibleObservationAsync(
        WindowsApplicationHost host,
        INantoWindow window,
        Phase1TestRequest request,
        string stage,
        string? screenshotPath,
        List<Phase1WindowObservation> observations,
        List<string> retainedArtifacts)
    {
        var nativeWindow = (WindowsWindow)window;
        if (screenshotPath is not null)
        {
            await host.Dispatcher.InvokeAsync(() => VisibleDesktopAutomation.CaptureScreenshot(
                GetWindowHandle(nativeWindow),
                Path.Combine(request.ArtifactDirectory, screenshotPath)));
            retainedArtifacts.Add(screenshotPath);
        }

        observations.Add(await host.Dispatcher.InvokeAsync(
            () => VisibleDesktopAutomation.CaptureWindow(GetWindowHandle(nativeWindow), window.Size, stage, screenshotPath)));
    }

    private static Phase1ResourceLedgerReport CreateEmptyResourceReport() => new()
    {
        Resources = [],
        TotalAcquired = 0,
        TotalReleased = 0,
        TotalActive = 0,
    };

    private static bool Intersects(Phase1WindowObservation window, Phase1MonitorObservation monitor) =>
        Math.Min(window.Right, monitor.WorkRight) > Math.Max(window.Left, monitor.WorkLeft)
        && Math.Min(window.Bottom, monitor.WorkBottom) > Math.Max(window.Top, monitor.WorkTop);

    private static async Task MoveAndObserveAsync(
        WindowsApplicationHost host,
        INantoWindow window,
        Phase1MonitorObservation monitor,
        Phase1TestRequest request,
        string stage,
        string screenshotPath,
        List<Phase1WindowObservation> observations,
        List<string> retainedArtifacts)
    {
        var nativeWindow = (WindowsWindow)window;
        await host.Dispatcher.InvokeAsync(() => VisibleDesktopAutomation.MoveWindow(GetWindowHandle(nativeWindow), monitor));
        await WaitForDpiAsync(host, GetWindowHandle(nativeWindow), monitor.EffectiveDpi);
        await CaptureVisibleObservationAsync(host, window, request, stage, screenshotPath, observations, retainedArtifacts);
        if (observations[^1].Dpi != monitor.EffectiveDpi)
        {
            throw new InvalidOperationException($"The window reported DPI {observations[^1].Dpi} instead of {monitor.EffectiveDpi}.");
        }

        if (!IsContainedByWorkArea(observations[^1], monitor))
        {
            throw new InvalidOperationException($"The window did not settle completely inside the '{stage}' monitor work area.");
        }
    }

    private static bool IsContainedByWorkArea(Phase1WindowObservation window, Phase1MonitorObservation monitor) =>
        window.Left >= monitor.WorkLeft
        && window.Top >= monitor.WorkTop
        && window.Right <= monitor.WorkRight
        && window.Bottom <= monitor.WorkBottom;

    private static void RequireFrameMatchesAppearance(Phase1WindowObservation observation, string appearance)
    {
        var expectedDarkFrame = string.Equals(appearance, "Dark", StringComparison.Ordinal);
        if (observation.NativeDarkFrame != expectedDarkFrame)
        {
            throw new InvalidOperationException($"The native frame and SPA appearance disagree at stage '{observation.Stage}'.");
        }
    }

    private static async Task<List<string>> WriteTopologyAsync(
        Phase1TestRequest request,
        VisibleDesktopAutomation.VisibleMonitor[] topology)
    {
        const string relativePath = "topology.json";
        await using var stream = new FileStream(
            Path.Combine(request.ArtifactDirectory, relativePath),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            stream,
            topology.Select(static monitor => monitor.Observation).ToArray(),
            Phase1TestJsonContext.Default.Phase1MonitorObservationArray);
        return [relativePath];
    }

    private static async Task WriteObservationsAsync(
        Phase1TestRequest request,
        List<Phase1WindowObservation> observations,
        List<string> retainedArtifacts)
    {
        const string relativePath = "observations.json";
        await using var stream = new FileStream(
            Path.Combine(request.ArtifactDirectory, relativePath),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, observations.ToArray(), Phase1TestJsonContext.Default.Phase1WindowObservationArray);
        retainedArtifacts.Add(relativePath);
    }

    private static async Task<string> WaitForAppearanceValueAsync(WindowsApplicationHost host)
    {
        const string prefix = "nanto:test:appearance:";
        return (await WaitForDiagnosticMessagePrefixAsync(host, prefix))[prefix.Length..];
    }

    private static async Task<string> QueryAppearanceWithF6Async(
        WindowsApplicationHost host,
        INantoWindow window,
        string? expected,
        List<string> appearanceObservations)
    {
        const string appearancePrefix = "nanto:test:appearance:";
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        while (true)
        {
            await host.Dispatcher.InvokeAsync(
                () => VisibleDesktopAutomation.SendF6(GetWindowHandle((WindowsWindow)window)),
                timeout.Token);
            var message = await WaitForDiagnosticMessagePrefixAsync(host, appearancePrefix, timeout.Token);
            var appearance = message[appearancePrefix.Length..];
            await WaitForDiagnosticMessageAsync(host, "nanto:test:key:F6", timeout.Token);
            if (expected is null || string.Equals(appearance, expected, StringComparison.Ordinal))
            {
                appearanceObservations.Add(appearance);
                return appearance;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task WaitForDiagnosticMessageAsync(
        WindowsApplicationHost host,
        string expected,
        CancellationToken cancellationToken)
    {
        var message = await WaitForDiagnosticMessagePrefixAsync(host, expected, cancellationToken);
        if (!string.Equals(message, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected diagnostic message '{expected}', but received '{message}'.");
        }
    }

    private static async Task<string> WaitForDiagnosticMessagePrefixAsync(WindowsApplicationHost host, string prefix)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        return await WaitForDiagnosticMessagePrefixAsync(host, prefix, timeout.Token);
    }

    private static async Task<string> WaitForDiagnosticMessagePrefixAsync(
        WindowsApplicationHost host,
        string prefix,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await host.WaitForDiagnosticMessageAsync(cancellationToken);
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return message;
            }
        }
    }

    private static async Task WaitForDpiAsync(WindowsApplicationHost host, nint window, uint expectedDpi)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        WindowSize? previousSize = null;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = await host.Dispatcher.InvokeAsync(
                () => (Dpi: VisibleDesktopAutomation.GetDpi(window), Size: host.PrimaryWindow!.Size),
                timeout.Token);
            if (snapshot.Dpi == expectedDpi && snapshot.Size == previousSize)
            {
                return;
            }

            previousSize = snapshot.Size;
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task WaitForForegroundFocusAsync(WindowsApplicationHost host, INantoWindow window)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var observation = await host.Dispatcher.InvokeAsync(
                () => VisibleDesktopAutomation.CaptureWindow(GetWindowHandle((WindowsWindow)window), window.Size, "Activation", null),
                timeout.Token);
            if (observation.IsForeground && observation.HasKeyboardFocus)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static ValueTask EnsureForegroundFocusAsync(WindowsApplicationHost host, INantoWindow window) =>
        host.Dispatcher.InvokeAsync(() => VisibleDesktopAutomation.EnsureForegroundFocus(GetWindowHandle((WindowsWindow)window)));

    private static async Task WaitForSizeAsync(INantoWindow window, WindowSize expected)
    {
        using var timeout = new CancellationTokenSource(Program.ActivationTimeout);
        while (window.Size != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static unsafe nint GetWindowHandle(WindowsWindow window) => (nint)window.Handle.Value;
}
