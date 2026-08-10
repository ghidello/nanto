namespace Nanto;

public sealed record WindowOptions
{
    public required string Title { get; init; }

    public WindowBounds InitialBounds { get; init; } = new(100, 100, 1024, 768);

    public bool StartVisible { get; init; } = true;

    public bool Resizable { get; init; } = true;

    public string InitialRoute { get; init; } = "/";
}