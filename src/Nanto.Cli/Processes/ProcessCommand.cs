namespace Nanto.Cli.Processes;

internal sealed record ProcessCommand
{
    public required string File { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

internal sealed record ProcessRunResult
{
    public required int ExitCode { get; init; }

    public required bool ForcedTermination { get; init; }

    public required string[] DiagnosticLines { get; init; }
}
