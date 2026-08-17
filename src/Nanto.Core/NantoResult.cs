namespace Nanto;

/// <summary>Represents either a successful command value or an expected, typed application error.</summary>
public readonly struct NantoResult<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed Nanto result does not contain a value.");

    public TError Error => !IsSuccess
        ? _error!
        : throw new InvalidOperationException("A successful Nanto result does not contain an error.");

    private NantoResult(T value)
    {
        _value = value;
        _error = default;
        IsSuccess = true;
    }

    private NantoResult(TError error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public static implicit operator NantoResult<T, TError>(T value) => new(value);

    public static implicit operator NantoResult<T, TError>(TError error) => new(error);
}

public static class NantoResult
{
    public static NantoResult<T, TError> Success<T, TError>(T value) => value;

    public static NantoResult<T, TError> Failure<T, TError>(TError error) => error;
}
