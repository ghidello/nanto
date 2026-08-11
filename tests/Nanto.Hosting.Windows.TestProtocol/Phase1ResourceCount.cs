namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1ResourceCount
{
    public required string Name { get; init; }

    public required int Active { get; init; }

    public required int Peak { get; init; }
}
