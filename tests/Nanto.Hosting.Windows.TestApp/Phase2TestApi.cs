using System.Runtime.CompilerServices;

namespace Nanto.Hosting.Windows.TestApp;

[NantoApi]
internal sealed class ProjectsApi(int minimumProjectId)
{
    private readonly int _minimumProjectId = minimumProjectId;
    private int _activeWaitCount;
    private int _cancellationCount;

    [NantoEvent]
    public NantoEvent<ProjectChange> Changed { get; } = new();

    internal int ActiveWaitCount => Volatile.Read(ref _activeWaitCount);

    internal int CancellationCount => Volatile.Read(ref _cancellationCount);

    [NantoCommand]
    public ValueTask<NantoResult<ProjectDetails, OpenProjectError>> OpenAsync(
        int projectId,
        NantoCommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<NantoResult<ProjectDetails, OpenProjectError>>(
            projectId >= _minimumProjectId ? new ProjectDetails(projectId, context.WindowId.Value.ToString("N")) : OpenProjectError.NotFound);
    }

    [NantoCommand]
    public ValueTask ChangeAsync(int projectId, CancellationToken cancellationToken) =>
        Changed.PublishAsync(new ProjectChange(projectId, "changed"), cancellationToken);

    [NantoCommand]
    public ValueTask<int> GetCancellationCountAsync() => ValueTask.FromResult(Volatile.Read(ref _cancellationCount));

    [NantoCommand]
    public ValueTask<int> GetActiveWaitCountAsync() => ValueTask.FromResult(Volatile.Read(ref _activeWaitCount));

    [NantoCommand]
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeWaitCount);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancellationCount);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeWaitCount);
        }
    }
}

[NantoApiPart<ProjectsApi>]
internal sealed class ProjectBuildsApi(int completedPercent)
{
    private readonly int _completedPercent = completedPercent;

    [NantoCommand]
    public async IAsyncEnumerable<NantoResult<BuildProgress, BuildError>> BuildAsync(
        int projectId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new BuildProgress(projectId, _completedPercent);
    }
}

internal sealed record ProjectDetails(int Id, string Window);

internal sealed record ProjectChange(int ProjectId, string Kind);

internal sealed record BuildProgress(int ProjectId, int Percent);

internal enum OpenProjectError
{
    NotFound,
}

internal enum BuildError
{
    Failed,
}
