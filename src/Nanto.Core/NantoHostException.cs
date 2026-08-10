namespace Nanto;

public sealed class NantoHostException : Exception
{
    public required NantoFailureStage Stage { get; init; }

    public required string Operation
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Operation));
            field = value;
        }
    }

    public int? NativeErrorCode { get; init; }

    public IReadOnlyList<Exception> CleanupExceptions
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = [];

    public NantoHostException(string message, Exception primaryFailure)
        : base(message, primaryFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(primaryFailure);
    }
}