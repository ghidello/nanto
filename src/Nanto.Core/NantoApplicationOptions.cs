using Microsoft.Extensions.Logging;

namespace Nanto;

public sealed record NantoApplicationOptions
{
    public required string ApplicationId { get; init; }

    public required WindowOptions PrimaryWindow { get; init; }

    public required IWebAssetProvider Assets { get; init; }

    public ShutdownMode ShutdownMode { get; init; } = ShutdownMode.OnPrimaryWindowClosed;

    public ILoggerFactory? LoggerFactory { get; init; }

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}