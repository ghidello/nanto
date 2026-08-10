using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nanto;

internal sealed record ValidatedApplicationOptions(
    ApplicationIdentity Identity,
    WindowOptions PrimaryWindow,
    IWebAssetProvider Assets,
    ShutdownMode ShutdownMode,
    ILoggerFactory LoggerFactory,
    TimeSpan ShutdownTimeout)
{
    private static readonly TimeSpan _maximumShutdownTimeout = TimeSpan.FromMinutes(5);

    public static ValidatedApplicationOptions Create(NantoApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PrimaryWindow);
        ArgumentNullException.ThrowIfNull(options.Assets);

        var identity = ApplicationIdentity.Parse(options.ApplicationId);
        ValidateWindowOptions(options.PrimaryWindow);

        if (!Enum.IsDefined(options.ShutdownMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ShutdownMode, "The shutdown mode is not supported.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ShutdownTimeout, TimeSpan.Zero, nameof(options.ShutdownTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ShutdownTimeout, _maximumShutdownTimeout, nameof(options.ShutdownTimeout));

        return new ValidatedApplicationOptions(
            identity,
            options.PrimaryWindow,
            options.Assets,
            options.ShutdownMode,
            options.LoggerFactory ?? NullLoggerFactory.Instance,
            options.ShutdownTimeout);
    }

    private static void ValidateWindowOptions(WindowOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);

        ArgumentOutOfRangeException.ThrowIfEqual(options.InitialBounds, default, nameof(options.InitialBounds));

        RouteValidation.ThrowIfInvalid(options.InitialRoute, nameof(options.InitialRoute));
    }
}