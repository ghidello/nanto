namespace Nanto.Hosting;

/// <summary>
/// Enforces the portable window lifecycle state machine for host implementations.
/// </summary>
public sealed class WindowLifecycle(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private int _state = (int)WindowState.Created;

    public WindowState State => (WindowState)Volatile.Read(ref _state);

    /// <summary>
    /// Performs a valid state transition and returns its timestamped notification.
    /// </summary>
    public WindowStateChangedEventArgs TransitionTo(WindowState newState, Exception? failure = null)
    {
        lock (_gate)
        {
            var oldState = (WindowState)_state;
            if (!CanTransition(oldState, newState))
            {
                throw new InvalidOperationException($"Window state cannot transition from {oldState} to {newState}.");
            }

            Volatile.Write(ref _state, (int)newState);
            return new WindowStateChangedEventArgs
            {
                OldState = oldState,
                NewState = newState,
                OccurredAt = _timeProvider.GetUtcNow(),
                Failure = failure,
            };
        }
    }

    private static bool CanTransition(WindowState oldState, WindowState newState) => (oldState, newState) switch
    {
        (WindowState.Created, WindowState.Initializing) => true,
        (WindowState.Initializing, WindowState.Running) => true,
        (WindowState.Created or WindowState.Initializing or WindowState.Running, WindowState.Failed) => true,
        (WindowState.Created or WindowState.Initializing or WindowState.Running or WindowState.Failed, WindowState.Closing) => true,
        (WindowState.Closing, WindowState.Closed) => true,
        _ => false,
    };
}
