using System.Diagnostics;
using System.Text.Json;

using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.IntegrationTestKit;

public static class Phase1TestProcessRunner
{
    private static readonly TimeSpan _terminationTimeout = TimeSpan.FromSeconds(10);

    public static async Task<Phase1TestRunResult> RunAsync(Phase1TestRunOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);

        var runId = Guid.NewGuid().ToString("N");
        var applicationId = $"com.nanto.phase1.{runId}";
        var artifactDirectory = Path.Combine(Path.GetFullPath(options.ArtifactRoot), runId);
        Directory.CreateDirectory(artifactDirectory);
        var requestPath = Path.Combine(artifactDirectory, "request.json");
        var pendingRequestPath = Path.Combine(artifactDirectory, "request.pending.json");
        var reportPath = Path.Combine(artifactDirectory, "report.json");
        var request = new Phase1TestRequest
        {
            ProtocolVersion = Phase1TestProtocol.CurrentVersion,
            Scenario = options.Scenario,
            PresentationMode = options.PresentationMode,
            ApplicationId = applicationId,
            FailureCheckpoint = options.FailureCheckpoint,
            IterationCount = options.IterationCount,
            ArtifactDirectory = artifactDirectory,
        };
        Phase1TestRequestValidator.Validate(request);

        Process? process = null;
        WindowsProcessJob? job = null;
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        var timedOut = false;
        try
        {
            job = WindowsProcessJob.CreateKillOnClose();
            process = StartProcess(options.TestAppPath, requestPath, reportPath);
            standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                job.Assign(process);
            }
            catch (Exception assignmentException)
            {
                try
                {
                    await StopUncontainedProcessAsync(process).ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    throw new AggregateException("Assigning TestApp to its Job Object failed, and stopping the uncontained process also failed.", assignmentException, stopException);
                }

                throw;
            }

            await WriteRequestAsync(pendingRequestPath, request, cancellationToken).ConfigureAwait(false);
            File.Move(pendingRequestPath, requestPath);

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(options.Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException timeoutException)
            {
                timedOut = true;
                try
                {
                    await StopContainedProcessAsync(process, job).ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    throw new AggregateException("The TestApp scenario timed out, and stopping its contained process tree also failed.", timeoutException, stopException);
                }
            }
            catch (OperationCanceledException cancellationException)
            {
                try
                {
                    await StopContainedProcessAsync(process, job).ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    throw new AggregateException("The TestApp scenario was canceled, and stopping its contained process tree also failed.", cancellationException, stopException);
                }

                throw;
            }

            // Root-process exit does not prove that every contained descendant exited or released inherited output handles.
            job.Dispose();
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            var report = await ReadReportAsync(reportPath, request, cancellationToken).ConfigureAwait(false);
            var result = new Phase1TestRunResult
            {
                ApplicationId = applicationId,
                ArtifactDirectory = artifactDirectory,
                ArtifactsRetained = true,
                ExitCode = process.ExitCode,
                TimedOut = timedOut,
                StandardOutput = output,
                StandardError = error,
                Report = report,
            };
            if (!result.Succeeded)
            {
                return result;
            }

            Directory.Delete(artifactDirectory, recursive: true);
            return result with { ArtifactsRetained = false };
        }
        catch (Exception processException) when (process is { HasExited: false } && job is not null)
        {
            try
            {
                await StopContainedProcessAsync(process, job).ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                throw new AggregateException("The TestApp run failed, and stopping its contained process tree also failed.", processException, stopException);
            }

            throw;
        }
        finally
        {
            job?.Dispose();
            process?.Dispose();
        }
    }

    private static async Task<Phase1TestReport?> ReadReportAsync(
        string reportPath,
        Phase1TestRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
        {
            return null;
        }

        var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            var report = await JsonSerializer.DeserializeAsync(stream, Phase1TestJsonContext.Default.Phase1TestReport, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The TestApp report contains JSON null.");
            if (report.ProtocolVersion != Phase1TestProtocol.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"The TestApp reported protocol version {report.ProtocolVersion}; expected {Phase1TestProtocol.CurrentVersion}.");
            }

            if (!string.Equals(report.Scenario, request.Scenario.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The TestApp reported scenario '{report.Scenario}'; expected '{request.Scenario}'.");
            }

            return report;
        }
    }

    private static Process StartProcess(string testAppPath, string requestPath, string reportPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(testAppPath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(testAppPath))!,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add("--report");
        startInfo.ArgumentList.Add(reportPath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The Phase 1 TestApp process did not start.");
    }

    private static async Task StopContainedProcessAsync(Process process, WindowsProcessJob job)
    {
        Exception? terminationFailure = null;
        try
        {
            job.Terminate();
        }
        catch (Exception exception)
        {
            terminationFailure = exception;
        }

        job.Dispose();
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(_terminationTimeout, CancellationToken.None).ConfigureAwait(false);
        if (terminationFailure is not null)
        {
            throw terminationFailure;
        }
    }

    private static async Task StopUncontainedProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(_terminationTimeout, CancellationToken.None).ConfigureAwait(false);
    }

    private static void ValidateOptions(Phase1TestRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TestAppPath);
        if (!Path.IsPathFullyQualified(options.TestAppPath) || !File.Exists(options.TestAppPath))
        {
            throw new ArgumentException("The TestApp path must identify an existing absolute file.", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.ArtifactRoot);
        if (!Path.IsPathFullyQualified(options.ArtifactRoot))
        {
            throw new ArgumentException("The artifact root must be an absolute path.", nameof(options));
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "The scenario timeout must be positive and no greater than five minutes.");
        }
    }

    private static async Task WriteRequestAsync(string pendingRequestPath, Phase1TestRequest request, CancellationToken cancellationToken)
    {
        var stream = new FileStream(pendingRequestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, request, Phase1TestJsonContext.Default.Phase1TestRequest, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
