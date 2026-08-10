namespace Nanto;

public sealed class ApplicationStateChangedEventArgs : EventArgs
{
    public required ApplicationState OldState { get; init; }

    public required ApplicationState NewState { get; init; }

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