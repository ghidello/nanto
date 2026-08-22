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

    public ValidatedContentSource Content { get; }

    internal NantoBridgeConfigurationSnapshot Bridge { get; }

    public ColorSchemePreference PreferredColorScheme { get; }

    public ShutdownMode ShutdownMode { get; }

    public ILoggerFactory LoggerFactory { get; }

    public TimeSpan ShutdownTimeout { get; }

    private ValidatedApplicationOptions(
        ApplicationIdentity identity,
        WindowOptions primaryWindow,
        ValidatedContentSource content,
        NantoBridgeConfigurationSnapshot bridge,
        ColorSchemePreference preferredColorScheme,
        ShutdownMode shutdownMode,
        ILoggerFactory loggerFactory,
        TimeSpan shutdownTimeout)
    {
        Identity = identity;
        PrimaryWindow = primaryWindow;
        Content = content;
        Bridge = bridge;
        PreferredColorScheme = preferredColorScheme;
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
        ArgumentNullException.ThrowIfNull(options.Content);
        ArgumentNullException.ThrowIfNull(options.Bridge);

        var identity = ApplicationIdentity.Parse(options.ApplicationId);
        ValidateWindowOptions(options.PrimaryWindow);
        ValidatedContentSource content = ValidateContent(options.Content);

        if (!Enum.IsDefined(options.PreferredColorScheme))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.PreferredColorScheme, "The preferred color scheme is not supported.");
        }

        if (!Enum.IsDefined(options.ShutdownMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ShutdownMode, "The shutdown mode is not supported.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ShutdownTimeout, TimeSpan.Zero, nameof(options.ShutdownTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ShutdownTimeout, _maximumShutdownTimeout, nameof(options.ShutdownTimeout));

        return new ValidatedApplicationOptions(
            identity,
            options.PrimaryWindow with { Capabilities = [.. options.PrimaryWindow.Capabilities] },
            content,
            options.Bridge.CaptureSnapshot(),
            options.PreferredColorScheme,
            options.ShutdownMode,
            options.LoggerFactory ?? NullLoggerFactory.Instance,
            options.ShutdownTimeout);
    }

    /// <summary>
    /// Creates the asset-preparation context associated with this validated application identity and a host-prepared storage root.
    /// </summary>
    public WebAssetPreparationContext CreateWebAssetPreparationContext(string applicationRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRootDirectory);
        if (!Path.IsPathFullyQualified(applicationRootDirectory))
        {
            throw new ArgumentException("The application storage root must be an absolute path.", nameof(applicationRootDirectory));
        }

        return new WebAssetPreparationContext(
            Identity.CanonicalId,
            Identity.StorageKey,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRootDirectory)),
            LoggerFactory);
    }

    private static void ValidateWindowOptions(WindowOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentNullException.ThrowIfNull(options.Capabilities);

        ArgumentOutOfRangeException.ThrowIfEqual(options.InitialSize, default, nameof(options.InitialSize));
    }

    private static ValidatedContentSource ValidateContent(NantoContentSource content)
    {
        return content switch
        {
            NantoProductionContent production => ValidateProductionContent(production),
            NantoDevelopmentContent development => ValidateDevelopmentContent(development),
            _ => throw new ArgumentException("The content source type is not supported.", nameof(content)),
        };
    }

    private static ValidatedProductionContent ValidateProductionContent(NantoProductionContent content)
    {
        ArgumentNullException.ThrowIfNull(content.Assets);
        RouteValidation.ThrowIfInvalid(content.InitialRoute, nameof(content.InitialRoute));
        return new(content.Assets, content.InitialRoute);
    }

    private static ValidatedDevelopmentContent ValidateDevelopmentContent(NantoDevelopmentContent content)
    {
        ArgumentNullException.ThrowIfNull(content.StartUri);
        Uri startUri = content.StartUri;
        if (!startUri.IsAbsoluteUri
            || startUri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(startUri.Host)
            || !string.IsNullOrEmpty(startUri.UserInfo))
        {
            throw new ArgumentException("Development content requires an absolute HTTP or HTTPS URI with a concrete host and no user information.", nameof(content));
        }

        var origin = new UriBuilder(startUri.Scheme, startUri.Host, startUri.Port).Uri;
        return new(startUri, origin);
    }
}
