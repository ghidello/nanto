namespace Nanto.Hosting.Windows.TestProtocol;

public enum Phase1TestScenario
{
    HostLifecycle,
    AcquisitionFailure,
    StartupCancellation,
    NativeClose,
    RunCancellation,
    RepeatedClose,
    Navigation,
    Appearance,
    RendererRecovery,
    BrowserProcessExit,
    SharedProfile,
    ContainmentTimeout,
    VisibleDesktop,
    VisibleCrossMonitorDpi,
    BridgeUnary,
    BridgeNavigation,
    BridgeClose,
    BridgeSecurity,
}
