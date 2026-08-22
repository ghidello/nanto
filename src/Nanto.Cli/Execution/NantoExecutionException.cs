namespace Nanto.Cli.Execution;

internal sealed class NantoExecutionException(string code, string stepId, string message, int exitCode = 0) : Exception(message)
{
    public string Code { get; } = code;

    public string StepId { get; } = stepId;

    public int ProcessExitCode { get; } = exitCode;
}
