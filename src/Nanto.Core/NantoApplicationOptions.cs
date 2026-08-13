using Microsoft.Extensions.Logging;

namespace Nanto;

public sealed record NantoApplicationOptions
{
    public required string ApplicationId { get; init; }

    public required WindowOptions PrimaryWindow { get; init; }

    public required IWebAssetProvider Assets { get; init; }

    /// <summary>
    /// Gets the application-wide color-scheme preference. The application owns persistence of a user-selected value.
    /// </summary>
    /// <remarks>
    /// <see cref="ColorSchemePreference.System" /> delegates to the platform and remains responsive to operating-system changes.
    /// </remarks>
    public ColorSchemePreference PreferredColorScheme { get; init; } = ColorSchemePreference.System;

    public ShutdownMode ShutdownMode { get; init; } = ShutdownMode.OnPrimaryWindowClosed;

    /// <summary>
    /// Gets the application-owned logger factory used by Nanto and its asset provider.
    /// </summary>
    /// <remarks>
    /// Nanto does not dispose the factory. A null value disables logging through
    /// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory" />.
    /// Failure to create a logger is treated as an application startup failure.
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; init; }

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
