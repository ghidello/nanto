namespace Nanto.Testing;

public enum LifecycleRecordKind
{
    ApplicationStateChanged,
    WindowStateChanged,
    RendererFailed,
}

public sealed class LifecycleRecord
{
    public long Sequence { get; }

    public LifecycleRecordKind Kind { get; }

    public DateTimeOffset RecordedAt { get; }

    public WindowId? WindowId { get; }

    public EventArgs EventArgs { get; }

    internal LifecycleRecord(long sequence, LifecycleRecordKind kind, DateTimeOffset recordedAt, WindowId? windowId, EventArgs eventArgs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        TimestampValidation.ThrowIfNotUtc(recordedAt, nameof(recordedAt));
        ArgumentNullException.ThrowIfNull(eventArgs);

        Sequence = sequence;
        Kind = kind;
        RecordedAt = recordedAt;
        WindowId = windowId;
        EventArgs = eventArgs;
    }
}