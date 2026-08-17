using System.Runtime.CompilerServices;

namespace Nanto.Hosting.Windows.TestApp;

[NantoApi]
internal sealed class ProjectsApi(int minimumProjectId)
{
    private readonly int _minimumProjectId = minimumProjectId;

    [NantoEvent]
    public NantoEvent<ProjectChange> Changed { get; } = new();

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
