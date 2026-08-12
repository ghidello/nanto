namespace Nanto.Hosting.Windows.TestProtocol;

public enum Phase1TestScenario
{
    HostLifecycle,
    AcquisitionFailure,
    NativeClose,
    RunCancellation,
    RepeatedClose,
    ContainmentTimeout,
}
