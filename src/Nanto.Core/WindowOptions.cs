namespace Nanto;

public sealed record WindowOptions
{
    public required string Title { get; init; }

    public WindowBounds InitialBounds { get; init; } = new(100, 100, 1024, 768);

    public bool StartVisible { get; init; } = true;

    public bool Resizable { get; init; } = true;

    /// <summary>
    /// Gets the root-relative URI of the declared asset used for initial navigation.
    /// </summary>
    /// <remarks>Query strings and fragments are permitted but do not participate in asset lookup.</remarks>
    public string InitialRoute { get; init; } = "/index.html";
}