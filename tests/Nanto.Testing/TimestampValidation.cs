namespace Nanto.Testing;

internal static class TimestampValidation
{
    public static void ThrowIfNotUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must use the UTC offset.", parameterName);
        }
    }
}
