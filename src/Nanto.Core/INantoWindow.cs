namespace Nanto;

public interface INantoWindow
{
    WindowId Id { get; }

    string Title { get; }

    WindowBounds Bounds { get; }

    WindowState State { get; }

    bool IsVisible { get; }

    event EventHandler<WindowStateChangedEventArgs>? StateChanged;

    event EventHandler<RendererFailedEventArgs>? RendererFailed;

    ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default);

    ValueTask SetBoundsAsync(WindowBounds bounds, CancellationToken cancellationToken = default);

    ValueTask ActivateAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}