namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1ObservedFailure
{
    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? FailureStage { get; init; }

    public string? Operation { get; init; }

    public string? PrimaryExceptionType { get; init; }

    public string? PrimaryMessage { get; init; }

    public required Phase1ExceptionDetail[] CleanupFailures { get; init; }
}
