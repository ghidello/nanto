using System.Text.Json;

namespace Nanto.Cli.TestProtocol;

public static class Phase3LifecycleProtocol
{
    public const int SchemaVersion = 1;
    public const string LinePrefix = "NANTO_LIFECYCLE ";

    public static string Serialize(Phase3LifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        return LinePrefix + JsonSerializer.Serialize(value, Phase3LifecycleJsonContext.Default.Phase3LifecycleEvent);
    }

    public static Phase3LifecycleEvent Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Lifecycle line has an invalid prefix.", nameof(line));
        }

        Phase3LifecycleEvent value = JsonSerializer.Deserialize(
            line.AsSpan(LinePrefix.Length),
            Phase3LifecycleJsonContext.Default.Phase3LifecycleEvent)
            ?? throw new ArgumentException("Lifecycle line has no event.", nameof(line));
        Validate(value);
        return value;
    }

    public static void Validate(Phase3LifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.SchemaVersion, "Lifecycle schema version is unsupported.");
        }

        if (!Enum.IsDefined(value.Marker))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Marker, "Lifecycle marker is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(value.Resource) || value.Resource.Length > 32 || !value.Resource.All(IsSymbolCharacter))
        {
            throw new ArgumentException("Lifecycle resource must be a bounded symbolic value.", nameof(value));
        }

        if (value.Sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Sequence, "Lifecycle sequence must not be negative.");
        }

        if (value.Reason is { } reason && (reason.Length > 64 || !reason.All(IsSymbolCharacter)))
        {
            throw new ArgumentException("Lifecycle reason must be a bounded symbolic value.", nameof(value));
        }
    }

    private static bool IsSymbolCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';
}
