namespace Nanto.Testing;

public enum FakeWindowMutationKind
{
    TitleChanged,
    SizeChanged,
    Activated,
    CloseRequested,
}

public sealed record FakeWindowMutation
{
    public required FakeWindowMutationKind Kind { get; init; }

    public required DateTimeOffset OccurredAt
    {
        get;
        init
        {
            TimestampValidation.ThrowIfNotUtc(value, nameof(OccurredAt));
            field = value;
        }
    }

    public string? Title { get; init; }

    public WindowSize? Size { get; init; }
}
