namespace Nanto.Hosting.Windows.TestProtocol;

public enum Phase1TestScenario
{
    HostLifecycle,
    AcquisitionFailure,
    StartupCancellation,
    NativeClose,
    RunCancellation,
    RepeatedClose,
    Appearance,
    SharedProfile,
    ContainmentTimeout,
}
