namespace Nanto;

public readonly record struct WindowId
{
    public Guid Value { get; }

    public WindowId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public static WindowId Create() => new(Guid.NewGuid());
}