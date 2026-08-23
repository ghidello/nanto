using System.Diagnostics;

namespace Nanto.Cli.Processes;

internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly WindowsProcessJob _job;
    private readonly BoundedLinePump _standardError;
    private readonly BoundedLinePump _standardOutput;
    private readonly Task _standardErrorPump;
    private readonly Task _standardOutputPump;
    private readonly WindowsProcessLauncher _process;
    private int _stopping;
    private bool _forcedTermination;

    public int Id => _process.Id;

    public bool HasExited => _process.Exit.IsCompleted;

    private OwnedProcess(
        WindowsProcessLauncher process,
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
        WindowsProcessLauncher? process = null;
        WindowsProcessJob? job = null;
        try
        {
            job = WindowsProcessJob.CreateKillOnClose();
            process = WindowsProcessLauncher.Start(startInfo, job);
            TextWriter synchronizedOutput = TextWriter.Synchronized(output);
            return new OwnedProcess(
                process,
                job,
                new BoundedLinePump(resource, synchronizedOutput),
                new BoundedLinePump(resource, synchronizedOutput));
        }
        catch
        {
            job?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    internal async Task<ProcessRunResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        await _process.Exit.WaitAsync(cancellationToken);
        await Task.WhenAll(_standardOutputPump, _standardErrorPump);
        return CreateResult();
    }

    internal async Task<ProcessRunResult> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        if (Interlocked.Exchange(ref _stopping, 1) == 0 && !_process.Exit.IsCompleted)
        {
            _process.TrySignalCtrlBreak();
            try
            {
                _process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (!_process.Exit.IsCompleted)
        {
            using var timeout = new CancellationTokenSource(gracefulTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await _process.Exit.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _forcedTermination = true;
                _job.Terminate();
            }
        }

        await _process.Exit.WaitAsync(cancellationToken);
        await Task.WhenAll(_standardOutputPump, _standardErrorPump);
        return CreateResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.Exit.IsCompleted)
        {
            await StopAsync(TimeSpan.Zero);
        }

        _job.Dispose();
        _process.Dispose();
    }

    private ProcessRunResult CreateResult() => new()
    {
        ExitCode = _process.GetExitCode(),
        ForcedTermination = _forcedTermination,
        DiagnosticLines = [.. _standardOutput.Snapshot(), .. _standardError.Snapshot()],
    };
}
