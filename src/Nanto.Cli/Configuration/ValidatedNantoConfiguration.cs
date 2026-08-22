namespace Nanto.Cli.Configuration;

internal sealed record ValidatedNantoConfiguration
{
    public required string ConfigurationPath { get; init; }

    public required string ApplicationRoot { get; init; }

    public required string HostProjectPath { get; init; }

    public required string FrontendDirectory { get; init; }

    public required string GeneratedClientDirectory { get; init; }

    public required string FrontendDistDirectory { get; init; }

    public required string FrontendEntryAssetPath { get; init; }

    public required Uri DevelopmentUri { get; init; }

    public required NantoConfiguration Value { get; init; }
}
