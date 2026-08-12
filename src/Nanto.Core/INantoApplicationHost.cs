namespace Nanto;

public interface INantoApplicationHost : IAsyncDisposable
{
    ApplicationState State { get; }

    IUiDispatcher Dispatcher { get; }

    INantoWindow? PrimaryWindow { get; }

    /// <summary>
    /// Gets a thread-safe snapshot of the selected application-wide color-scheme preference.
    /// </summary>
    /// <remarks>
    /// <see cref="ColorSchemePreference.System" /> follows the platform preference through the host profile. Nanto does not persist this value.
    /// </remarks>
    ColorSchemePreference PreferredColorScheme { get; }

    event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;

    Task RunAsync(NantoApplicationOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the application-wide color-scheme preference after the host has been created.
    /// </summary>
    /// <remarks>
    /// The operation is thread-safe and completes only after the platform profile accepts the change. The snapshot changes after successful application.
    /// The application remains responsible for persisting any user choice. Calling before creation or during shutdown is invalid.
    /// </remarks>
    ValueTask SetPreferredColorSchemeAsync(ColorSchemePreference preferredColorScheme, CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
