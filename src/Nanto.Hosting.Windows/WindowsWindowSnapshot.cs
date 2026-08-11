namespace Nanto.Hosting.Windows;

internal sealed record WindowsWindowSnapshot(string Title, WindowBounds Bounds, WindowState State, bool IsVisible);
