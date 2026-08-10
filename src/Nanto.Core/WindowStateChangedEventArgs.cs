namespace Nanto;

public sealed class WindowStateChangedEventArgs : EventArgs
{
    public required WindowState OldState { get; init; }

    public required WindowState NewState { get; init; }

    public required DateTimeOffset OccurredAt
    {
        get;
        init
        {
            TimestampValidation.ThrowIfNotUtc(value, nameof(OccurredAt));
            field = value;
        }
    }

    public Exception? Failure { get; init; }
}