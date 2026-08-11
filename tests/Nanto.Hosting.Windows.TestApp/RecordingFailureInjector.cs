namespace Nanto.Hosting.Windows.TestApp;

internal sealed class RecordingFailureInjector(
    Phase1AcquisitionCheckpoint? failureCheckpoint,
    Func<ResourceLedgerSnapshot>? captureFailureSnapshot = null) : IPhase1FailureInjector
{
    private readonly List<Phase1AcquisitionCheckpoint> _reachedCheckpoints = [];

    public InvalidOperationException? InjectedFailure { get; private set; }

    public ResourceLedgerSnapshot? SnapshotAtFailure { get; private set; }

    public IReadOnlyList<Phase1AcquisitionCheckpoint> ReachedCheckpoints => _reachedCheckpoints;

    public void OnAcquired(Phase1AcquisitionCheckpoint checkpoint)
    {
        _reachedCheckpoints.Add(checkpoint);
        if (checkpoint == failureCheckpoint)
        {
            SnapshotAtFailure = captureFailureSnapshot?.Invoke();
            InjectedFailure = new InvalidOperationException($"Injected failure after acquiring {checkpoint}.");
            throw InjectedFailure;
        }
    }
}
