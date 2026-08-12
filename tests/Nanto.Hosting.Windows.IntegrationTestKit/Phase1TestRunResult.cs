using Nanto.Hosting.Windows.TestProtocol;

namespace Nanto.Hosting.Windows.IntegrationTestKit;

public sealed record Phase1TestRunResult
{
    public required string ApplicationId { get; init; }

    public required string ArtifactDirectory { get; init; }

    public required bool ArtifactsRetained { get; init; }

    public required int? ExitCode { get; init; }

    public required bool TimedOut { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public Phase1TestReport? Report { get; init; }

    public bool Succeeded => !TimedOut && ExitCode == 0 && Report?.Succeeded == true;
}
