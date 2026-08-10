namespace Nanto;

public sealed class RendererFailedEventArgs : EventArgs
{
    public required RendererFailureKind Kind { get; init; }

    public required string Description
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Description));
            field = value;
        }
    }

    public required bool WillAttemptRecovery { get; init; }

    public required DateTimeOffset OccurredAt
    {
        get;
        init
        {
            TimestampValidation.ThrowIfNotUtc(value, nameof(OccurredAt));
            field = value;
        }
    }
}