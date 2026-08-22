namespace Nanto;

public sealed record WindowOptions
{
    public required string Title { get; init; }

    /// <summary>
    /// Gets the initial size of the window's client content area in device-independent pixels.
    /// </summary>
    /// <remarks>The platform chooses the initial display and screen position.</remarks>
    public WindowSize InitialSize { get; init; } = new(1024, 768);

    public bool StartVisible { get; init; } = true;

    public bool Resizable { get; init; } = true;

    /// <summary>Gets the generated frontend capabilities granted to this window.</summary>
    public IReadOnlyList<NantoFrontendCapability> Capabilities { get; init; } = [];

    /// <summary>
    /// Gets the root-relative URI of the declared asset used for initial navigation.
    /// </summary>
    /// <remarks>Query strings and fragments are permitted but do not participate in asset lookup.</remarks>
}
