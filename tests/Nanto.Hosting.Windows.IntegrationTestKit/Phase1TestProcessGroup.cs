namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed class Phase1TestProcessGroup : IDisposable
{
    private readonly Lock _gate = new();
    private WindowsProcessJob? _job;

    private Phase1TestProcessGroup(WindowsProcessJob job)
    {
        _job = job;
    }

    public static Phase1TestProcessGroup Create() => new(WindowsProcessJob.CreateKillOnClose());

    public void Dispose()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _job, null)?.Dispose();
        }
    }

    internal WindowsProcessJob GetJob()
    {
        lock (_gate)
        {
            return _job ?? throw new ObjectDisposedException(nameof(Phase1TestProcessGroup));
        }
    }

    internal void Assign(System.Diagnostics.Process process)
    {
        lock (_gate)
        {
            (_job ?? throw new ObjectDisposedException(nameof(Phase1TestProcessGroup))).Assign(process);
        }
    }
}
