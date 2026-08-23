using System.Text.Json;

namespace Nanto.Plugin.TestProtocol;

public static class Phase4PluginTestProtocol
{
    public const int SchemaVersion = 1;
    public const string LinePrefix = "NANTO_PLUGIN_LIFECYCLE ";

    public static string Serialize(Phase4PluginLifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        return LinePrefix + JsonSerializer.Serialize(value, Phase4PluginTestJsonContext.Default.Phase4PluginLifecycleEvent);
    }

    public static Phase4PluginLifecycleEvent Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin lifecycle line has an invalid prefix.", nameof(line));
        }

        Phase4PluginLifecycleEvent value = JsonSerializer.Deserialize(
            line.AsSpan(LinePrefix.Length),
            Phase4PluginTestJsonContext.Default.Phase4PluginLifecycleEvent)
            ?? throw new ArgumentException("Plugin lifecycle line has no event.", nameof(line));
        Validate(value);
        return value;
    }

    public static void Validate(Phase4PluginLifecycleEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.SchemaVersion, "Plugin lifecycle schema version is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(value.Plugin)
            || value.Plugin.Length > 64
            || !value.Plugin.All(IsPluginIdentifierCharacter))
        {
            throw new ArgumentException("Plugin identifier must be a bounded lowercase symbolic value.", nameof(value));
        }

        if (!Enum.IsDefined(value.Scope))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Scope, "Plugin scope is unsupported.");
        }

        if (!Enum.IsDefined(value.Transition))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Transition, "Plugin lifecycle transition is unsupported.");
        }

        if (value.Sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Sequence, "Plugin lifecycle sequence must not be negative.");
        }
    }

    private static bool IsPluginIdentifierCharacter(char character) => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '.' or '-';
}