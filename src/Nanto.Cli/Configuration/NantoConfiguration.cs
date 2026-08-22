using System.Text.Json.Serialization;

namespace Nanto.Cli.Configuration;

internal sealed record NantoConfiguration
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public required int SchemaVersion { get; init; }

    public required NantoApplicationConfiguration Application { get; init; }

    public required NantoFrontendConfiguration Frontend { get; init; }

    public NantoBuildConfiguration Build { get; init; } = new();
}

internal sealed record NantoApplicationConfiguration
{
    public required string HostProject { get; init; }
}

internal sealed record NantoFrontendConfiguration
{
    public required string Directory { get; init; }

    public required string GeneratedClient { get; init; }

    public NantoCommandConfiguration? Install { get; init; }

    public required NantoDevelopmentConfiguration Dev { get; init; }

    public required NantoFrontendBuildConfiguration Build { get; init; }
}

internal record NantoCommandConfiguration
{
    public required string File { get; init; }

    public required string[] Arguments { get; init; }
}

internal sealed record NantoDevelopmentConfiguration
{
    public string? File { get; init; }

    public string[]? Arguments { get; init; }

    public required string Url { get; init; }

    public required int ReadyTimeoutSeconds { get; init; }

    [JsonIgnore]
    public bool IsManaged => File is not null;
}

internal sealed record NantoFrontendBuildConfiguration : NantoCommandConfiguration
{
    public required string Dist { get; init; }

    public string? EntryAsset { get; init; }
}

internal sealed record NantoBuildConfiguration
{
    public string Runtime { get; init; } = "auto";
}
