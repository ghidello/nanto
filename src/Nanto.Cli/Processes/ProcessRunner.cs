namespace Nanto.Cli.Processes;

internal sealed class ProcessRunner
{
    private readonly TextWriter _output;
    private readonly TimeSpan _shutdownTimeout;

    internal ProcessRunner(TextWriter output, TimeSpan? shutdownTimeout = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10);
    }

    internal OwnedProcess Start(ProcessCommand command, string resource) => OwnedProcess.Start(command, resource, _output);

    internal async Task<ProcessRunResult> RunAsync(ProcessCommand command, string resource, CancellationToken cancellationToken)
    {
        await using OwnedProcess process = Start(command, resource);
        try
        {
            return await process.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await process.StopAsync(_shutdownTimeout, CancellationToken.None);
            throw;
        }
    }
}
