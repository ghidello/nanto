namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1ResourceOwnershipEvent
{
    public required long Sequence { get; init; }

    public required long LeaseId { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string Action { get; init; }
}
