namespace Nanto;

/// <summary>Describes the mutually exclusive production or development content loaded by a Nanto application.</summary>
public abstract record NantoContentSource;

/// <summary>Uses application-packaged web assets for production content.</summary>
public sealed record NantoProductionContent : NantoContentSource
{
    public required IWebAssetProvider Assets { get; init; }

    public string InitialRoute { get; init; } = "/index.html";
}

/// <summary>Uses an explicitly selected development-server URI.</summary>
/// <remarks>
/// Selecting development content grants bridge authority only to the URI's exact normalized origin. Production publishes should not select this mode
/// from ambient environment alone.
/// </remarks>
public sealed record NantoDevelopmentContent : NantoContentSource
{
    public required Uri StartUri { get; init; }
}
