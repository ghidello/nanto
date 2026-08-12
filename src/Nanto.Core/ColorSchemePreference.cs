namespace Nanto;

/// <summary>
/// Selects the color scheme that Nanto asks a platform host and its web content to use.
/// </summary>
public enum ColorSchemePreference
{
    /// <summary>
    /// Follows the operating-system preference and continues to react to system changes.
    /// </summary>
    System,

    /// <summary>
    /// Uses a light color scheme independently from the operating-system preference.
    /// </summary>
    Light,

    /// <summary>
    /// Uses a dark color scheme independently from the operating-system preference.
    /// </summary>
    Dark,
}
