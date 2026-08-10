namespace Nanto;

internal sealed class ApplicationLifecycle(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private int _state = (int)ApplicationState.NotStarted;

    public ApplicationState State => (ApplicationState)Volatile.Read(ref _state);

    public ApplicationStateChangedEventArgs TransitionTo(ApplicationState newState, Exception? failure = null)
    {
        lock (_gate)
        {
            var oldState = (ApplicationState)_state;
            if (!CanTransition(oldState, newState))
            {
                throw new InvalidOperationException($"Application state cannot transition from {oldState} to {newState}.");
            }

            Volatile.Write(ref _state, (int)newState);
            return new ApplicationStateChangedEventArgs
            {
                OldState = oldState,
                NewState = newState,
                OccurredAt = _timeProvider.GetUtcNow(),
                Failure = failure,
            };
        }
    }

    private static bool CanTransition(ApplicationState oldState, ApplicationState newState) => (oldState, newState) switch
    {
        (ApplicationState.NotStarted, ApplicationState.Creating) => true,
        (ApplicationState.Creating, ApplicationState.Created) => true,
        (ApplicationState.Created or ApplicationState.Deactivated, ApplicationState.Activated) => true,
        (ApplicationState.Activated, ApplicationState.Deactivated) => true,
        (ApplicationState.NotStarted or ApplicationState.Creating or ApplicationState.Created or ApplicationState.Activated or ApplicationState.Deactivated,
            ApplicationState.Failed) => true,
        (ApplicationState.Creating or ApplicationState.Created or ApplicationState.Activated or ApplicationState.Deactivated or ApplicationState.Failed,
            ApplicationState.Closing) => true,
        (ApplicationState.Closing, ApplicationState.Closed) => true,
        _ => false,
    };
}