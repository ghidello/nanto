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
    SharedProfile,
    ContainmentTimeout,
}