using System.Text.Json.Serialization;

namespace Nanto.Cli.Diagnostics;

internal sealed record NantoDoctorReport
{
    public int SchemaVersion { get; init; } = 1;

    public required string ConfigurationSchema { get; init; }

    public required string Platform { get; init; }

    public required string Architecture { get; init; }

    public required string RuntimeVersion { get; init; }

    public required string RuntimeProfile { get; init; }

    public required NantoDoctorCheck[] Checks { get; init; }
}

internal sealed record NantoDoctorCheck
{
    public required string Name { get; init; }

    public required string Status { get; init; }

    public required string Detail { get; init; }

    public string? DiagnosticPath { get; init; }

    public string? Remediation { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NantoDoctorReport))]
internal sealed partial class NantoDoctorJsonContext : JsonSerializerContext;
