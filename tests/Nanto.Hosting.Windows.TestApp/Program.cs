using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.TestApp;

internal static class Program
{
    private const int FailedExitCode = 1;
    private const int InvalidInvocationExitCode = 2;
    private static readonly TimeSpan _activationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _requestPublicationTimeout = TimeSpan.FromSeconds(15);

    internal static TimeSpan ActivationTimeout => _activationTimeout;

    public static async Task<int> Main(string[] args)
    {
        CommandLine commandLine;
        try
        {
            commandLine = CommandLine.Parse(args);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return InvalidInvocationExitCode;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        Phase1TestRequest? request = null;
        Phase1TestReport report;
        try
        {
            await using var requestStream = await OpenRequestAsync(commandLine.RequestPath);
            request = await JsonSerializer.DeserializeAsync(requestStream, Phase1TestJsonContext.Default.Phase1TestRequest)
                ?? throw new InvalidDataException("The request document contains JSON null.");
            Phase1TestRequestValidator.Validate(request);
            if (request.IterationCount != 1)
            {
                throw new InvalidDataException("The implemented Milestone 2 scenarios require an iteration count of exactly one.");
            }

            if (!Directory.Exists(request.ArtifactDirectory))
            {
                throw new DirectoryNotFoundException($"The artifact directory does not exist: '{request.ArtifactDirectory}'.");
            }

            report = await RunScenarioAsync(request, startedAt, stopwatch);
        }
        catch (Exception exception)
        {
            report = CreateFailedReport(request?.Scenario.ToString() ?? "InvalidRequest", startedAt, stopwatch, exception);
        }

        await WriteReportAsync(commandLine.ReportPath, report);
        return report.Succeeded ? 0 : FailedExitCode;
    }

    internal static Phase1ObservedFailure DescribeFailure(Exception exception) => CreateObservedFailure(exception);

    private static Phase1HostEnvironment CaptureHostEnvironment() => new()
    {
        FrameworkDescription = RuntimeInformation.FrameworkDescription,
        OperatingSystemDescription = RuntimeInformation.OSDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeVersion = Environment.Version.ToString(),
    };

    private static Phase1TestReport CreateFailedReport(string scenario, DateTimeOffset startedAt, Stopwatch stopwatch, Exception exception)
    {
        var emptyResources = CreateEmptyResourceReport();
        stopwatch.Stop();
        return new Phase1TestReport
        {
            ProtocolVersion = Phase1TestProtocol.CurrentVersion,
            Scenario = scenario,
            Succeeded = false,
            HostEnvironment = CaptureHostEnvironment(),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            ReachedCheckpoints = [],
            LifecycleTransitions = [],
            InitialResources = emptyResources,
            PeakResources = emptyResources,
            FinalResources = emptyResources,
            ResourceOwnershipEvents = [],
            RendererRecoveryResult = "NotApplicable",
            RetainedArtifactPaths = [],
            ObservedFailure = CreateObservedFailure(exception),
        };
    }

    private static Phase1ResourceLedgerReport CreateEmptyResourceReport() => new()
    {
        Resources = [],
        TotalAcquired = 0,
        TotalReleased = 0,
        TotalActive = 0,
    };

    private static Phase1ObservedFailure CreateObservedFailure(Exception exception)
    {
        var hostException = exception as NantoHostException;
        return new Phase1ObservedFailure
        {
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            FailureStage = hostException?.Stage.ToString(),
            Operation = hostException?.Operation,
            PrimaryExceptionType = exception.InnerException?.GetType().FullName,
            PrimaryMessage = exception.InnerException?.Message,
            CleanupFailures = hostException?.CleanupExceptions.Select(CreateExceptionDetail).ToArray() ?? [],
        };
    }

    private static Phase1ExceptionDetail CreateExceptionDetail(Exception exception) => new()
    {
        ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
        Message = exception.Message,
    };

    private static async Task<Phase1TestReport> RunScenarioAsync(
        Phase1TestRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch)
    {
        return request.Scenario switch
        {
            Phase1TestScenario.HostLifecycle => await ScenarioRunner.RunHostLifecycleAsync(request, startedAt, stopwatch),
            Phase1TestScenario.AcquisitionFailure => await ScenarioRunner.RunAcquisitionFailureAsync(request, startedAt, stopwatch),
            Phase1TestScenario.NativeClose => await ScenarioRunner.RunNativeCloseAsync(request, startedAt, stopwatch),
            Phase1TestScenario.RunCancellation => await ScenarioRunner.RunCancellationAsync(request, startedAt, stopwatch),
            Phase1TestScenario.RepeatedClose => await ScenarioRunner.RunRepeatedCloseAsync(request, startedAt, stopwatch),
            Phase1TestScenario.ContainmentTimeout => await WaitForContainmentAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Scenario, "The scenario is not supported."),
        };
    }

    private static async Task<Phase1TestReport> WaitForContainmentAsync()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        throw new UnreachableException();
    }

    private static async Task<FileStream> OpenRequestAsync(string requestPath)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!File.Exists(requestPath))
        {
            if (Stopwatch.GetElapsedTime(startedAt) >= _requestPublicationTimeout)
            {
                throw new TimeoutException($"The request document was not published within {_requestPublicationTimeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
    }

    private static async Task WriteReportAsync(string reportPath, Phase1TestReport report)
    {
        var parentDirectory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("The report path does not have a parent directory.");
        Directory.CreateDirectory(parentDirectory);
        await using var reportStream = new FileStream(reportPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(reportStream, report, Phase1TestJsonContext.Default.Phase1TestReport);
    }

    private sealed record CommandLine(string RequestPath, string ReportPath)
    {
        public static CommandLine Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args is not ["--request", var requestPath, "--report", var reportPath])
            {
                throw new ArgumentException("Expected exactly: --request <absolute-json-path> --report <absolute-json-path>.", nameof(args));
            }

            return new CommandLine(ValidateAbsolutePath(requestPath, "request"), ValidateAbsolutePath(reportPath, "report"));
        }

        private static string ValidateAbsolutePath(string path, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException($"The {name} path must be absolute.", name);
            }

            return Path.GetFullPath(path);
        }
    }
}
