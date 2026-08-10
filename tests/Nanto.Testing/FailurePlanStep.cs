namespace Nanto.Testing;

public sealed record FailurePlanStep
{
    public required string Operation { get; init; }

    public Exception? Failure { get; init; }
}