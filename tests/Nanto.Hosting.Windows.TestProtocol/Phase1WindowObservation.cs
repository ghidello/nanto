namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1WindowObservation
{
    public required string Stage { get; init; }

    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int Right { get; init; }

    public required int Bottom { get; init; }

    public required double ClientWidth { get; init; }

    public required double ClientHeight { get; init; }

    public required uint Dpi { get; init; }

    public required bool IsForeground { get; init; }

    public required bool HasKeyboardFocus { get; init; }

    public required bool NativeDarkFrame { get; init; }

    public string? ScreenshotPath { get; init; }
}
