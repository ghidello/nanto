using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nanto.Hosting;

/// <summary>
/// Contains the validated, canonical application configuration consumed by host implementations.
/// </summary>
public sealed record ValidatedApplicationOptions
{
    private static readonly TimeSpan _maximumShutdownTimeout = TimeSpan.FromMinutes(5);

    public ApplicationIdentity Identity { get; }

    public WindowOptions PrimaryWindow { get; }

    public IWebAssetProvider Assets { get; }

    public ShutdownMode ShutdownMode { get; }

    public ILoggerFactory LoggerFactory { get; }

    public TimeSpan ShutdownTimeout { get; }

    private ValidatedApplicationOptions(
        ApplicationIdentity identity,
        WindowOptions primaryWindow,
        IWebAssetProvider assets,
        ShutdownMode shutdownMode,
        ILoggerFactory loggerFactory,
        TimeSpan shutdownTimeout)
    {
        Identity = identity;
        PrimaryWindow = primaryWindow;
        Assets = assets;
        ShutdownMode = shutdownMode;
        LoggerFactory = loggerFactory;
        ShutdownTimeout = shutdownTimeout;
    }

    /// <summary>
    /// Validates an application configuration and returns its canonical host representation.
    /// </summary>
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

    /// <summary>
    /// Creates the asset-preparation context associated with this validated application identity.
    /// </summary>
    public WebAssetPreparationContext CreateWebAssetPreparationContext() =>
        new(Identity.CanonicalId, Identity.StorageKey);

    private static void ValidateWindowOptions(WindowOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);

        ArgumentOutOfRangeException.ThrowIfEqual(options.InitialBounds, default, nameof(options.InitialBounds));

        RouteValidation.ThrowIfInvalid(options.InitialRoute, nameof(options.InitialRoute));
    }
}
