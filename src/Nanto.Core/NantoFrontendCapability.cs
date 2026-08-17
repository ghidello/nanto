using System.ComponentModel;

namespace Nanto;

public enum NantoFrontendCapabilityKind
{
    Command,
    Event,
}

/// <summary>Identifies one generated frontend capability that may be granted to a window.</summary>
public readonly record struct NantoFrontendCapability
{
    public uint Id { get; }

    public string SymbolicName { get; }

    public NantoFrontendCapabilityKind Kind { get; }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public NantoFrontendCapability(uint id, string symbolicName, NantoFrontendCapabilityKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolicName);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The frontend capability kind is not supported.");
        }

        Id = id;
        SymbolicName = symbolicName;
        Kind = kind;
    }
}
