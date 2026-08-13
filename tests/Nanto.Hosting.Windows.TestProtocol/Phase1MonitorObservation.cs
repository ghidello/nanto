namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1MonitorObservation
{
    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int Right { get; init; }

    public required int Bottom { get; init; }

    public required int WorkLeft { get; init; }

    public required int WorkTop { get; init; }

    public required int WorkRight { get; init; }

    public required int WorkBottom { get; init; }

    public required bool IsPrimary { get; init; }

    public required uint EffectiveDpi { get; init; }
}
