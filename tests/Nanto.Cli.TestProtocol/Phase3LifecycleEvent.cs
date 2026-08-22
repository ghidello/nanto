namespace Nanto.Cli.TestProtocol;

public sealed record Phase3LifecycleEvent
{
    public int SchemaVersion { get; init; } = Phase3LifecycleProtocol.SchemaVersion;

    public required Phase3LifecycleMarker Marker { get; init; }

    public required string Resource { get; init; }

    public required int Sequence { get; init; }

    public string? Reason { get; init; }
}
