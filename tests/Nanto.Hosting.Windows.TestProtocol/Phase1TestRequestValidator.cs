namespace Nanto.Hosting.Windows.TestProtocol;

public static class Phase1TestRequestValidator
{
    public static void Validate(Phase1TestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != Phase1TestProtocol.CurrentVersion)
        {
            throw new ArgumentException(
                $"Unsupported protocol version {request.ProtocolVersion}; expected {Phase1TestProtocol.CurrentVersion}.",
                nameof(request));
        }

        if (!Enum.IsDefined(request.Scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Scenario, "The scenario is not supported.");
        }

        if (!Enum.IsDefined(request.PresentationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.PresentationMode, "The presentation mode is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApplicationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.IterationCount, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactDirectory);
        if (!Path.IsPathFullyQualified(request.ArtifactDirectory))
        {
            throw new ArgumentException("The artifact directory must be an absolute path.", nameof(request));
        }

        if (request.Scenario == Phase1TestScenario.AcquisitionFailure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureCheckpoint);
        }
        else if (request.FailureCheckpoint is not null)
        {
            throw new ArgumentException("A failure checkpoint is valid only for the acquisition-failure scenario.", nameof(request));
        }
    }
}
