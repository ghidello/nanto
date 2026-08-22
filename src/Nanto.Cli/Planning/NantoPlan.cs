using System.Text.Json.Serialization;

namespace Nanto.Cli.Planning;

internal sealed record NantoPlan
{
    public int SchemaVersion { get; init; } = 1;

    public required string Command { get; init; }

    public required string Runtime { get; init; }

    public required string RuntimeReason { get; init; }

    public required NantoPlanPath[] Paths { get; init; }

    public required NantoPlanStep[] Steps { get; init; }

    public required string[] ShutdownOrder { get; init; }
}

internal sealed record NantoPlanPath
{
    public required string Label { get; init; }

    public required string RelativePath { get; init; }
}

internal sealed record NantoPlanStep
{
    public required string Id { get; init; }

    public required string[] DependsOn { get; init; }

    public required string Kind { get; init; }

    public required string WorkingDirectory { get; init; }

    public string? File { get; init; }

    public string[] Arguments { get; init; } = [];

    public required string Mutation { get; init; }

    public int? TimeoutSeconds { get; init; }

    public string? ReadinessUrl { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NantoPlan))]
internal sealed partial class NantoPlanJsonContext : JsonSerializerContext;
