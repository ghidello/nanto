using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.IntegrationTestKit;

public static class Phase1TestProcessRunner
{
    private static readonly TimeSpan _terminationTimeout = TimeSpan.FromSeconds(10);

    public static async Task<Phase1TestRunResult> RunAsync(Phase1TestRunOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);

        var runId = options.ArtifactDirectoryName ?? Guid.NewGuid().ToString("N");
        var applicationId = options.ApplicationId ?? $"com.nanto.phase1.{runId}";
        var applicationRoot = GetApplicationRoot(applicationId);
        var artifactDirectory = Path.Combine(Path.GetFullPath(options.ArtifactRoot), runId);
        if (Directory.Exists(artifactDirectory) || File.Exists(artifactDirectory))
        {
            throw new IOException($"The artifact directory '{runId}' already exists.");
        }

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
            CoordinationDirectory = options.CoordinationDirectory,
            ParticipantId = options.ParticipantId,
        };
        Phase1TestRequestValidator.Validate(request);

        Process? process = null;
        WindowsProcessJob? job = null;
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        var timedOut = false;
        var ownsJob = options.ProcessGroup is null;
        try
        {
            job = options.ProcessGroup?.GetJob() ?? WindowsProcessJob.CreateKillOnClose();
            process = StartProcess(options.TestAppPath, requestPath, reportPath);
            standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                if (options.ProcessGroup is null)
                {
                    job.Assign(process);
                }
                else
                {
                    options.ProcessGroup.Assign(process);
                }
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
                    await StopContainedProcessAsync(process, job, ownsJob).ConfigureAwait(false);
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
                    await StopContainedProcessAsync(process, job, ownsJob).ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    throw new AggregateException("The TestApp scenario was canceled, and stopping its contained process tree also failed.", cancellationException, stopException);
                }

                await PersistOutputAsync(artifactDirectory, standardOutput, standardError).ConfigureAwait(false);
                throw;
            }

            // Root-process exit does not prove that every contained descendant exited or released inherited output handles.
            if (ownsJob)
            {
                job.Dispose();
            }
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            // Write output before interpreting the report. Successful non-retained runs remove the entire directory below,
            // while failures keep the diagnostics even when report loading or validation fails.
            await PersistOutputAsync(artifactDirectory, output, error).ConfigureAwait(false);
            var report = await ReadReportAsync(reportPath, request, cancellationToken).ConfigureAwait(false);
            var result = new Phase1TestRunResult
            {
                ApplicationId = applicationId,
                ApplicationRoot = applicationRoot,
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

            if (options.CleanupApplicationRootOnSuccess && Directory.Exists(applicationRoot))
            {
                await DeleteApplicationRootAsync(applicationRoot, CancellationToken.None).ConfigureAwait(false);
            }

            if (!options.RetainArtifactsOnSuccess)
            {
                Directory.Delete(artifactDirectory, recursive: true);
                return result with { ArtifactsRetained = false };
            }

            return result;
        }
        catch (Exception processException) when (process is { HasExited: false } && job is not null)
        {
            try
            {
                await StopContainedProcessAsync(process, job, ownsJob).ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                throw new AggregateException("The TestApp run failed, and stopping its contained process tree also failed.", processException, stopException);
            }

            if (standardOutput is not null && standardError is not null)
            {
                await PersistOutputAsync(artifactDirectory, standardOutput, standardError).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (ownsJob)
            {
                job?.Dispose();
            }
            process?.Dispose();
        }
    }

    public static async Task DeleteApplicationRootAsync(string applicationRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        var applicationsRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nanto",
            "applications"));
        var fullApplicationRoot = Path.GetFullPath(applicationRoot);
        if (!string.Equals(Path.GetDirectoryName(fullApplicationRoot), applicationsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The application root must be a direct child of Nanto's production applications directory.", nameof(applicationRoot));
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (Directory.Exists(fullApplicationRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(fullApplicationRoot, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                && Stopwatch.GetElapsedTime(startedAt) < _terminationTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string GetApplicationRoot(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var canonicalId = applicationId.Trim().ToLowerInvariant();
        var finalSegment = canonicalId.Split('.')[^1];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId));
        var storageKey = $"{finalSegment}-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "Nanto", "applications", storageKey);
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

    private static async Task PersistOutputAsync(string artifactDirectory, Task<string> standardOutput, Task<string> standardError)
    {
        await PersistOutputAsync(artifactDirectory, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task PersistOutputAsync(string artifactDirectory, string standardOutput, string standardError)
    {
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "stdout.txt"), standardOutput, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "stderr.txt"), standardError, CancellationToken.None).ConfigureAwait(false);
    }

    private static Process StartProcess(string testAppPath, string requestPath, string reportPath)
    {
        var extension = Path.GetExtension(testAppPath);
        var isFrameworkDependent = string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase);
        if (!isFrameworkDependent && !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The TestApp path must identify a .NET assembly or Windows executable.", nameof(testAppPath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = isFrameworkDependent ? "dotnet" : Path.GetFullPath(testAppPath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(testAppPath))!,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (isFrameworkDependent)
        {
            startInfo.ArgumentList.Add(Path.GetFullPath(testAppPath));
        }

        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add("--report");
        startInfo.ArgumentList.Add(reportPath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The Phase 1 TestApp process did not start.");
    }

    private static async Task StopContainedProcessAsync(Process process, WindowsProcessJob job, bool disposeJob)
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

        if (disposeJob)
        {
            job.Dispose();
        }
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

        if (options.ArtifactDirectoryName is not null && !IsValidArtifactDirectoryName(options.ArtifactDirectoryName))
        {
            throw new ArgumentException(
                "The artifact directory name must contain 1 to 128 lowercase ASCII letters, digits, or hyphens.",
                nameof(options));
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "The scenario timeout must be positive and no greater than five minutes.");
        }

        if (options.CoordinationDirectory is not null && !Path.IsPathFullyQualified(options.CoordinationDirectory))
        {
            throw new ArgumentException("The coordination directory must be an absolute path.", nameof(options));
        }
    }

    private static bool IsValidArtifactDirectoryName(string value)
    {
        if (value.Length is 0 or > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
            {
                return false;
            }
        }

        return true;
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
