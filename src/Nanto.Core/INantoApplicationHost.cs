namespace Nanto;

public interface INantoApplicationHost : IAsyncDisposable
{
    ApplicationState State { get; }

    IUiDispatcher Dispatcher { get; }

    INantoWindow? PrimaryWindow { get; }

    event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;

    Task RunAsync(NantoApplicationOptions options, CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}