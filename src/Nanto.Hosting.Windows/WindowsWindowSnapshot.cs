namespace Nanto.Hosting.Windows;

internal sealed record WindowsWindowSnapshot(string Title, WindowSize Size, WindowState State, bool IsVisible);
