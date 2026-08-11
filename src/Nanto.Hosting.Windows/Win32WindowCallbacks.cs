namespace Nanto.Hosting.Windows;

internal sealed class Win32WindowCallbacks
{
    public Action? CloseRequested { get; init; }

    public Action? Destroyed { get; init; }
}
