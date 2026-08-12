namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1TestRequest
{
    public required int ProtocolVersion { get; init; }

    public required Phase1TestScenario Scenario { get; init; }

    public required Phase1TestPresentationMode PresentationMode { get; init; }

    public required string ApplicationId { get; init; }

    public string? FailureCheckpoint { get; init; }

    public required int IterationCount { get; init; }

    public required string ArtifactDirectory { get; init; }

    public string? CoordinationDirectory { get; init; }

    public string? ParticipantId { get; init; }
}
