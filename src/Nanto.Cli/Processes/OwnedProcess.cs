using System.Diagnostics;

namespace Nanto.Cli.Processes;

internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly WindowsProcessJob _job;
    private readonly BoundedLinePump _standardError;
    private readonly BoundedLinePump _standardOutput;
    private readonly Task _standardErrorPump;
    private readonly Task _standardOutputPump;
    private readonly Process _process;
    private int _stopping;
    private bool _forcedTermination;

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    private OwnedProcess(
        Process process,
        WindowsProcessJob job,
        BoundedLinePump standardOutput,
        BoundedLinePump standardError)
    {
        _process = process;
        _job = job;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _standardOutputPump = standardOutput.RunAsync(process.StandardOutput);
        _standardErrorPump = standardError.RunAsync(process.StandardError);
    }

    internal static OwnedProcess Start(ProcessCommand command, string resource, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentNullException.ThrowIfNull(output);
        ProcessStartInfo startInfo = CommandLauncher.Create(command);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        WindowsProcessJob? job = null;
        bool started = false;
        try
        {
            job = WindowsProcessJob.CreateKillOnClose();
            if (!process.Start())
            {
                throw new InvalidOperationException("The configured process did not start.");
            }

            started = true;
            job.Assign(process);
            TextWriter synchronizedOutput = TextWriter.Synchronized(output);
            return new OwnedProcess(
                process,
                job,
                new BoundedLinePump(resource, synchronizedOutput),
                new BoundedLinePump(resource, synchronizedOutput));
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            job?.Dispose();
            process.Dispose();
            throw;
        }
    }

    internal async Task<ProcessRunResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(_standardOutputPump, _standardErrorPump);
        return CreateResult();
    }

    internal async Task<ProcessRunResult> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        if (Interlocked.Exchange(ref _stopping, 1) == 0 && !_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
                _process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (!_process.HasExited)
        {
            using var timeout = new CancellationTokenSource(gracefulTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await _process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _forcedTermination = true;
                _job.Terminate();
            }
        }

        await _process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(_standardOutputPump, _standardErrorPump);
        return CreateResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            await StopAsync(TimeSpan.Zero);
        }

        _job.Dispose();
        _process.Dispose();
    }

    private ProcessRunResult CreateResult() => new()
    {
        ExitCode = _process.ExitCode,
        ForcedTermination = _forcedTermination,
        DiagnosticLines = [.. _standardOutput.Snapshot(), .. _standardError.Snapshot()],
    };
}
