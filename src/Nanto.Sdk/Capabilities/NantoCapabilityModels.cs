namespace Nanto.Sdk.Capabilities;

internal sealed record NantoCapabilityInput(string Path, string DisplayPath);

internal sealed record NantoCapabilityPermissionCatalogEntry(
    string Identifier,
    string[] Members,
    uint[] MemberIds,
    string? ScopeSchemaPath,
    string? ScopeSchemaSha256);

internal sealed record NantoCompiledCapabilityPolicy
{
    public required string Fingerprint { get; init; }

    public required NantoCompiledCapabilityEntry[] Entries { get; init; }
}

internal sealed record NantoCompiledCapabilityEntry
{
    public required string Window { get; init; }

    public required string Origin { get; init; }

    public required string Permission { get; init; }

    public required string[] Members { get; init; }

    public required uint[] MemberIds { get; init; }

    public string? ScopeJson { get; init; }

    public string? ScopeSha256 { get; init; }
}

internal sealed record NantoCapabilityInspectionDocument
{
    public int SchemaVersion { get; init; } = 1;

    public required string Fingerprint { get; init; }

    public required NantoCapabilityInspectionEntry[] Entries { get; init; }
}

internal sealed record NantoCapabilityInspectionEntry
{
    public required string Window { get; init; }

    public required string Origin { get; init; }

    public required string Permission { get; init; }

    public required string[] Members { get; init; }

    public required uint[] MemberIds { get; init; }

    public string? ScopeSha256 { get; init; }
}
