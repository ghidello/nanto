namespace Nanto;

public interface INantoWindow
{
    WindowId Id { get; }

    string Title { get; }

    /// <summary>
    /// Gets a thread-safe snapshot of the client content size in device-independent pixels.
    /// </summary>
    WindowSize Size { get; }

    WindowState State { get; }

    bool IsVisible { get; }

    event EventHandler<WindowStateChangedEventArgs>? StateChanged;

    event EventHandler<RendererFailedEventArgs>? RendererFailed;

    ValueTask SetTitleAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the client content size while leaving screen placement under platform control.
    /// </summary>
    ValueTask SetSizeAsync(WindowSize size, CancellationToken cancellationToken = default);

    ValueTask ActivateAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
