namespace Nanto.Hosting.Windows;

internal sealed class DispatcherSynchronizationContext : SynchronizationContext
{
    private readonly Func<bool> _checkAccess;
    private readonly Action<SendOrPostCallback, object?> _post;

    public DispatcherSynchronizationContext(Func<bool> checkAccess, Action<SendOrPostCallback, object?> post)
    {
        _checkAccess = checkAccess ?? throw new ArgumentNullException(nameof(checkAccess));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _post(callback, state);
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_checkAccess())
        {
            throw new NotSupportedException("Synchronous cross-thread dispatcher calls are not supported.");
        }

        callback(state);
    }
}