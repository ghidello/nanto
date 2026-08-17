namespace Nanto;

/// <summary>Provides portable information about the window and origin that invoked a command.</summary>
public sealed class NantoCommandContext
{
    public WindowId WindowId { get; }

    public Uri Origin { get; }

    public IUiDispatcher UiDispatcher { get; }

    internal NantoCommandContext(WindowId windowId, Uri origin, IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        WindowId = windowId;
        Origin = origin;
        UiDispatcher = uiDispatcher;
    }
}
