namespace Nanto.Hosting.Windows.Tests;

internal sealed class DelegatePhase1FailureInjector(Action<Phase1AcquisitionCheckpoint> onAcquired) : IPhase1FailureInjector
{
    public void OnAcquired(Phase1AcquisitionCheckpoint checkpoint)
    {
        onAcquired(checkpoint);
    }
}
