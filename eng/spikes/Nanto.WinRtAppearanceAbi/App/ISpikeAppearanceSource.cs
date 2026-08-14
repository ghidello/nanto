namespace Nanto.WinRtAppearanceAbiSpike;

internal interface ISpikeAppearanceSource : IDisposable
{
    bool IsDark { get; }

    int NotificationCount { get; }

    void InvokeSynthetic();

    void SuppressCallbacks();
}