namespace Nanto.Hosting.Windows;

internal enum Phase1AcquisitionCheckpoint
{
    ApplicationHostStarted,
    UiThreadStarted,
    NativeMessageQueueCreated,
    DispatcherCreated,
    AssetLeasePrepared,
    WebViewEnvironmentCreated,
    WindowClassRegistered,
    WindowCreated,
    WebViewControllerCreated,
    WebViewCreated,
    WebViewProfileCreated,
    WebViewSettingsCreated,
    WebViewSettingsConfigured,
    NavigationCompletedSubscriptionAdded,
    WebMessageSubscriptionAdded,
    VirtualHostMappingAdded,
    InitialNavigationCompleted,
}

internal interface IPhase1FailureInjector
{
    void OnAcquired(Phase1AcquisitionCheckpoint checkpoint);
}

internal sealed class NoOpPhase1FailureInjector : IPhase1FailureInjector
{
    public static NoOpPhase1FailureInjector Instance { get; } = new();

    private NoOpPhase1FailureInjector()
    {
    }

    public void OnAcquired(Phase1AcquisitionCheckpoint checkpoint)
    {
    }
}
