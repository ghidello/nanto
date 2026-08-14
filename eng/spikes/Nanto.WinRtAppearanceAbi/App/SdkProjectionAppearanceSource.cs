#if SDK_PROJECTION
using Windows.UI.ViewManagement;

using WinRT;

namespace Nanto.WinRtAppearanceAbiSpike;

internal sealed class SdkProjectionAppearanceSource : ISpikeAppearanceSource
{
    private readonly UISettings _settings = new();
    private int _disposed;
    private int _notificationCount;
    private int _suppressed;

    public bool IsDark
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Windows.UI.Color foreground = _settings.GetColorValue(UIColorType.Foreground);
            return 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        }
    }

    public int NotificationCount => Volatile.Read(ref _notificationCount);

    public SdkProjectionAppearanceSource()
    {
        _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settings.ColorValuesChanged -= OnColorValuesChanged;
        ((IWinRTObject)_settings).NativeObject.Dispose();
    }

    public void InvokeSynthetic()
    {
        OnColorValuesChanged(_settings, null!);
    }

    public void SuppressCallbacks()
    {
        Volatile.Write(ref _suppressed, 1);
    }

    private void OnColorValuesChanged(UISettings sender, object eventArgs)
    {
        if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _suppressed) == 0)
        {
            Interlocked.Increment(ref _notificationCount);
        }
    }
}
#endif