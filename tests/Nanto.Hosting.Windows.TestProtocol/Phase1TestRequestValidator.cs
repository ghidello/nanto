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

        if (request.Scenario is Phase1TestScenario.AcquisitionFailure or Phase1TestScenario.StartupCancellation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureCheckpoint);
        }
        else if (request.FailureCheckpoint is not null)
        {
            throw new ArgumentException("A checkpoint is valid only for acquisition-failure and startup-cancellation scenarios.", nameof(request));
        }

        if (request.Scenario == Phase1TestScenario.SharedProfile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.CoordinationDirectory);
            if (!Path.IsPathFullyQualified(request.CoordinationDirectory))
            {
                throw new ArgumentException("The shared-profile coordination directory must be absolute.", nameof(request));
            }

            if (request.ParticipantId is not ("first" or "second"))
            {
                throw new ArgumentException("A shared-profile participant must be named 'first' or 'second'.", nameof(request));
            }
        }
        else if (request.CoordinationDirectory is not null || request.ParticipantId is not null)
        {
            throw new ArgumentException("Coordination settings are valid only for the shared-profile scenario.", nameof(request));
        }
    }
}
