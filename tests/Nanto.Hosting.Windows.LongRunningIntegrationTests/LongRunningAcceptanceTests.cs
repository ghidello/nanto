using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

using Nanto.Hosting.Windows.IntegrationTestKit;
using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.LongRunningIntegrationTests;

public sealed class LongRunningAcceptanceTests
{
    private const int BlockCount = 10;
    private const int HostLifecycleProcessesPerBlock = 50;
    private const int RendererRecoveryProcessesPerBlock = 5;
    private const int TotalProcessCount = BlockCount * (HostLifecycleProcessesPerBlock + RendererRecoveryProcessesPerBlock);
    private static readonly TimeSpan _outerTimeout = TimeSpan.FromMinutes(75);
    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(45);
    private static readonly string _repositoryRoot = GetAssemblyMetadata("NantoRepositoryRoot");
    private static readonly string _testAppPath = GetAssemblyMetadata("NantoTestAppPath");

    [Fact]
    public async Task LifecycleAndRendererRecoveryRemainStableAcrossFiveHundredFiftyProcesses()
    {
        var runId = Guid.NewGuid().ToString("N");
        var applicationId = $"com.nanto.phase1.soak.{runId}";
        var applicationRoot = Phase1TestProcessRunner.GetApplicationRoot(applicationId);
        var runDirectory = Path.Combine(_repositoryRoot, "artifacts", "phase1", "long-running", runId);
        var childArtifactRoot = Path.Combine(runDirectory, "children");
        Directory.CreateDirectory(childArtifactRoot);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var blockSummaries = new List<LongRunningBlockSummary>(BlockCount);
        var maximumResources = new Dictionary<string, Phase1ResourceCount>(StringComparer.Ordinal);
        var completedHostLifecycleProcesses = 0;
        var completedRendererRecoveryProcesses = 0;
        var completedBlocks = 0;
        var activeBlock = 0;
        var activeIteration = 0;
        Phase1TestScenario? activeScenario = null;
        var activeArtifactPath = "children";
        var hostEnvironment = new Phase1HostEnvironment
        {
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            OperatingSystemDescription = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeVersion = Environment.Version.ToString(),
        };
        LongRunningFailure? firstFailure = null;
        var status = LongRunningStatus.Passed;

        using var deadline = new CancellationTokenSource(_outerTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        await WriteProgressAsync(
            runDirectory,
            runId,
            startedAt,
            stopwatch,
            status: "Starting",
            currentBlock: 0,
            currentProcess: 0,
            completedHostLifecycleProcesses,
            completedRendererRecoveryProcesses);
        Console.WriteLine(
            $"Long-running acceptance started: 0/{TotalProcessCount} processes. " +
            $"Progress: '{Path.Combine(runDirectory, "progress.json")}'.");
        using (var processGroup = Phase1TestProcessGroup.Create())
        {
            try
            {
                for (var block = 1; block <= BlockCount && firstFailure is null; block++)
                {
                    var blockStartedAt = Stopwatch.GetTimestamp();
                    var blockHostLifecycleProcesses = 0;
                    var blockRendererRecoveryProcesses = 0;

                    for (var iteration = 1; iteration <= HostLifecycleProcessesPerBlock && firstFailure is null; iteration++)
                    {
                        activeBlock = block;
                        activeIteration = iteration;
                        activeScenario = Phase1TestScenario.HostLifecycle;
                        activeArtifactPath = GetRelativeArtifactPath(runDirectory, childArtifactRoot, activeScenario.Value, block, iteration);
                        var currentProcess = completedHostLifecycleProcesses + completedRendererRecoveryProcesses + 1;
                        await WriteProgressAsync(
                            runDirectory,
                            runId,
                            startedAt,
                            stopwatch,
                            status: "Running",
                            block,
                            currentProcess,
                            completedHostLifecycleProcesses,
                            completedRendererRecoveryProcesses,
                            Phase1TestScenario.HostLifecycle,
                            iteration);
                        var outcome = await RunChildAsync(
                            Phase1TestScenario.HostLifecycle,
                            block,
                            iteration,
                            applicationId,
                            childArtifactRoot,
                            runDirectory,
                            processGroup,
                            cancellation.Token);
                        firstFailure = outcome.Failure;
                        if (outcome.Report is not null)
                        {
                            hostEnvironment = outcome.Report.HostEnvironment;
                            ObserveResources(maximumResources, outcome.Report);
                        }

                        if (firstFailure is null)
                        {
                            completedHostLifecycleProcesses++;
                            blockHostLifecycleProcesses++;
                        }

                        await WriteProgressAsync(
                            runDirectory,
                            runId,
                            startedAt,
                            stopwatch,
                            status: firstFailure is null ? "Running" : "Failed",
                            block,
                            currentProcess,
                            completedHostLifecycleProcesses,
                            completedRendererRecoveryProcesses,
                            Phase1TestScenario.HostLifecycle,
                            iteration);
                    }

                    for (var iteration = 1; iteration <= RendererRecoveryProcessesPerBlock && firstFailure is null; iteration++)
                    {
                        activeBlock = block;
                        activeIteration = iteration;
                        activeScenario = Phase1TestScenario.RendererRecovery;
                        activeArtifactPath = GetRelativeArtifactPath(runDirectory, childArtifactRoot, activeScenario.Value, block, iteration);
                        var currentProcess = completedHostLifecycleProcesses + completedRendererRecoveryProcesses + 1;
                        await WriteProgressAsync(
                            runDirectory,
                            runId,
                            startedAt,
                            stopwatch,
                            status: "Running",
                            block,
                            currentProcess,
                            completedHostLifecycleProcesses,
                            completedRendererRecoveryProcesses,
                            Phase1TestScenario.RendererRecovery,
                            iteration);
                        var outcome = await RunChildAsync(
                            Phase1TestScenario.RendererRecovery,
                            block,
                            iteration,
                            applicationId,
                            childArtifactRoot,
                            runDirectory,
                            processGroup,
                            cancellation.Token);
                        firstFailure = outcome.Failure;
                        if (outcome.Report is not null)
                        {
                            hostEnvironment = outcome.Report.HostEnvironment;
                            ObserveResources(maximumResources, outcome.Report);
                        }

                        if (firstFailure is null)
                        {
                            completedRendererRecoveryProcesses++;
                            blockRendererRecoveryProcesses++;
                        }

                        await WriteProgressAsync(
                            runDirectory,
                            runId,
                            startedAt,
                            stopwatch,
                            status: firstFailure is null ? "Running" : "Failed",
                            block,
                            currentProcess,
                            completedHostLifecycleProcesses,
                            completedRendererRecoveryProcesses,
                            Phase1TestScenario.RendererRecovery,
                            iteration);
                    }

                    blockSummaries.Add(new LongRunningBlockSummary
                    {
                        BlockNumber = block,
                        CompletedHostLifecycleProcesses = blockHostLifecycleProcesses,
                        CompletedRendererRecoveryProcesses = blockRendererRecoveryProcesses,
                        DurationMilliseconds = (long)Stopwatch.GetElapsedTime(blockStartedAt).TotalMilliseconds,
                    });
                    if (firstFailure is null)
                    {
                        completedBlocks++;
                        Console.WriteLine(
                            $"Long-running acceptance completed block {completedBlocks}/{BlockCount} " +
                            $"({completedHostLifecycleProcesses + completedRendererRecoveryProcesses}/{TotalProcessCount} processes).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                status = deadline.IsCancellationRequested ? LongRunningStatus.TimedOut : LongRunningStatus.Canceled;
                firstFailure ??= new LongRunningFailure
                {
                    BlockNumber = activeBlock == 0 ? completedBlocks + 1 : activeBlock,
                    Iteration = activeIteration,
                    Scenario = activeScenario?.ToString() ?? "OuterRun",
                    Reason = status == LongRunningStatus.TimedOut ? "The 75-minute outer deadline expired." : "The test run was canceled.",
                    RelativeArtifactPath = activeArtifactPath,
                };
                processGroup.Dispose();
            }
            catch (Exception exception)
            {
                status = LongRunningStatus.Failed;
                firstFailure ??= new LongRunningFailure
                {
                    BlockNumber = activeBlock == 0 ? completedBlocks + 1 : activeBlock,
                    Iteration = activeIteration,
                    Scenario = activeScenario?.ToString() ?? "OuterRun",
                    Reason = $"{exception.GetType().Name}: aggregate execution failed.",
                    RelativeArtifactPath = activeArtifactPath,
                };
                processGroup.Dispose();
            }
        }

        if (firstFailure is not null && status == LongRunningStatus.Passed)
        {
            status = LongRunningStatus.Failed;
        }

        var applicationRootDeleted = false;
        string? applicationRootDeletionFailure = null;
        try
        {
            await Phase1TestProcessRunner.DeleteApplicationRootAsync(applicationRoot, CancellationToken.None);
            applicationRootDeleted = !Directory.Exists(applicationRoot);
        }
        catch (Exception exception)
        {
            applicationRootDeletionFailure = $"{exception.GetType().Name}: application-root deletion failed.";
        }

        if (!applicationRootDeleted && status == LongRunningStatus.Passed)
        {
            status = LongRunningStatus.Failed;
            firstFailure = new LongRunningFailure
            {
                BlockNumber = BlockCount,
                Iteration = RendererRecoveryProcessesPerBlock,
                Scenario = "FinalCleanup",
                Reason = applicationRootDeletionFailure ?? "The shared application root remained after deletion.",
                RelativeArtifactPath = ".",
            };
        }

        stopwatch.Stop();
        var summary = new LongRunningSummary
        {
            SchemaVersion = 1,
            RunId = runId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds,
            RequestedBlocks = BlockCount,
            CompletedBlocks = completedBlocks,
            RequestedHostLifecycleProcesses = BlockCount * HostLifecycleProcessesPerBlock,
            CompletedHostLifecycleProcesses = completedHostLifecycleProcesses,
            RequestedRendererRecoveryProcesses = BlockCount * RendererRecoveryProcessesPerBlock,
            CompletedRendererRecoveryProcesses = completedRendererRecoveryProcesses,
            Blocks = [.. blockSummaries],
            MaximumObservedResources = [.. maximumResources.Values.OrderBy(static resource => resource.Name, StringComparer.Ordinal)],
            SdkVersion = GetAssemblyMetadata("NantoSdkVersion"),
            TestAppLaunchKind = "FrameworkDependentDll",
            HostEnvironment = hostEnvironment,
            FirstFailure = firstFailure,
            FinalApplicationRootDeleted = applicationRootDeleted,
            FinalApplicationRootDeletionFailure = applicationRootDeletionFailure,
        };
        var summaryPath = await WriteSummaryAsync(runDirectory, summary);

        Assert.True(status == LongRunningStatus.Passed,
            $"Long-running acceptance ended with status {status}. Evidence retained at '{summaryPath}'. Failure: {firstFailure?.Reason}");
        Assert.Equal(BlockCount * HostLifecycleProcessesPerBlock, completedHostLifecycleProcesses);
        Assert.Equal(BlockCount * RendererRecoveryProcessesPerBlock, completedRendererRecoveryProcesses);
        Assert.Equal(BlockCount, completedBlocks);
        Assert.True(applicationRootDeleted);
    }

    private static string GetAssemblyMetadata(string key) => typeof(LongRunningAcceptanceTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
        .Value
        ?? throw new InvalidOperationException($"Assembly metadata '{key}' does not contain a value.");

    private static async Task WriteProgressAsync(
        string runDirectory,
        string runId,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        string status,
        int currentBlock,
        int currentProcess,
        int completedHostLifecycleProcesses,
        int completedRendererRecoveryProcesses,
        Phase1TestScenario? currentScenario = null,
        int? currentScenarioIteration = null)
    {
        var progress = new LongRunningProgress
        {
            SchemaVersion = 1,
            RunId = runId,
            Status = status,
            StartedAt = startedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds,
            CurrentBlock = currentBlock,
            TotalBlocks = BlockCount,
            CurrentProcess = currentProcess,
            TotalProcesses = TotalProcessCount,
            CompletedProcesses = completedHostLifecycleProcesses + completedRendererRecoveryProcesses,
            CompletedHostLifecycleProcesses = completedHostLifecycleProcesses,
            CompletedRendererRecoveryProcesses = completedRendererRecoveryProcesses,
            CurrentScenario = currentScenario?.ToString(),
            CurrentScenarioIteration = currentScenarioIteration,
        };
        var pendingPath = Path.Combine(runDirectory, "progress.pending.json");
        var progressPath = Path.Combine(runDirectory, "progress.json");
        var stream = new FileStream(pendingPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, progress, LongRunningJsonContext.Default.LongRunningProgress, CancellationToken.None);
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                File.Move(pendingPath, progressPath, overwrite: true);
                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < 39)
            {
                // Windows readers do not necessarily share deletion. Preserve the previous complete snapshot and retry after the reader closes.
                await Task.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Progress is diagnostic evidence and must never invalidate the lifecycle soak. A later child retries with a fresh snapshot.
                return;
            }
        }
    }

    private static void ObserveResources(Dictionary<string, Phase1ResourceCount> maximumResources, Phase1TestReport report)
    {
        foreach (var resource in report.PeakResources.Resources)
        {
            if (!maximumResources.TryGetValue(resource.Name, out var current))
            {
                maximumResources[resource.Name] = resource;
                continue;
            }

            maximumResources[resource.Name] = resource with
            {
                Active = Math.Max(current.Active, resource.Active),
                Peak = Math.Max(current.Peak, resource.Peak),
            };
        }
    }

    private static string GetRelativeArtifactPath(
        string runDirectory,
        string childArtifactRoot,
        Phase1TestScenario scenario,
        int block,
        int iteration)
    {
        return Path.GetRelativePath(runDirectory, Path.Combine(childArtifactRoot, GetArtifactDirectoryName(scenario, block, iteration)));
    }

    private static async Task<ChildOutcome> RunChildAsync(
        Phase1TestScenario scenario,
        int block,
        int iteration,
        string applicationId,
        string childArtifactRoot,
        string runDirectory,
        Phase1TestProcessGroup processGroup,
        CancellationToken cancellationToken)
    {
        var artifactDirectoryName = GetArtifactDirectoryName(scenario, block, iteration);
        var relativeArtifactPath = Path.GetRelativePath(runDirectory, Path.Combine(childArtifactRoot, artifactDirectoryName));
        try
        {
            var result = await Phase1TestProcessRunner.RunAsync(new Phase1TestRunOptions
            {
                TestAppPath = _testAppPath,
                ArtifactRoot = childArtifactRoot,
                ArtifactDirectoryName = artifactDirectoryName,
                Scenario = scenario,
                ApplicationId = applicationId,
                PresentationMode = Phase1TestPresentationMode.Hidden,
                CleanupApplicationRootOnSuccess = false,
                RetainArtifactsOnSuccess = false,
                ProcessGroup = processGroup,
                Timeout = _processTimeout,
            }, cancellationToken);
            var reason = GetFailureReason(result, scenario);
            return new ChildOutcome
            {
                Report = result.Report,
                Failure = reason is null
                    ? null
                    : new LongRunningFailure
                    {
                        BlockNumber = block,
                        Iteration = iteration,
                        Scenario = scenario.ToString(),
                        Reason = reason,
                        RelativeArtifactPath = relativeArtifactPath,
                    },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ChildOutcome
            {
                Failure = new LongRunningFailure
                {
                    BlockNumber = block,
                    Iteration = iteration,
                    Scenario = scenario.ToString(),
                    Reason = $"{exception.GetType().Name}: child execution failed.",
                    RelativeArtifactPath = relativeArtifactPath,
                },
            };
        }
    }

    private static string GetArtifactDirectoryName(Phase1TestScenario scenario, int block, int iteration)
    {
        var scenarioName = scenario == Phase1TestScenario.HostLifecycle ? "lifecycle" : "recovery";
        return $"block-{block:D2}-{scenarioName}-{iteration:D3}";
    }

    private static string? GetFailureReason(Phase1TestRunResult result, Phase1TestScenario scenario)
    {
        if (result.TimedOut)
        {
            return "The child process exceeded its 45-second timeout.";
        }

        if (result.ExitCode != 0)
        {
            return $"The child process exited with code {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.";
        }

        if (result.Report is null)
        {
            return "The child process did not produce a report.";
        }

        if (!result.Report.Succeeded)
        {
            return $"The child report failed with {result.Report.ObservedFailure?.ExceptionType ?? "an unknown failure"}.";
        }

        if (result.Report.FinalResources.TotalActive != 0)
        {
            return $"The child report retained {result.Report.FinalResources.TotalActive} resources.";
        }

        if (scenario == Phase1TestScenario.RendererRecovery && !string.Equals(result.Report.RendererRecoveryResult, "Exited:True:Reloaded", StringComparison.Ordinal))
        {
            return $"Renderer recovery reported the bounded result '{result.Report.RendererRecoveryResult}'.";
        }

        return null;
    }

    private static async Task<string> WriteSummaryAsync(string runDirectory, LongRunningSummary summary)
    {
        var pendingPath = Path.Combine(runDirectory, "summary.pending.json");
        var summaryPath = Path.Combine(runDirectory, "summary.json");
        var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, summary, LongRunningJsonContext.Default.LongRunningSummary, CancellationToken.None);
        }

        File.Move(pendingPath, summaryPath);
        File.Delete(Path.Combine(runDirectory, "progress.json"));
        return summaryPath;
    }

    private sealed record ChildOutcome
    {
        public Phase1TestReport? Report { get; init; }

        public LongRunningFailure? Failure { get; init; }
    }
}
