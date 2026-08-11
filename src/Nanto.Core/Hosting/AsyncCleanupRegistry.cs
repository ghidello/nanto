namespace Nanto.Hosting;

/// <summary>
/// Collects asynchronously released resources and drains them in reverse acquisition order.
/// </summary>
/// <remarks>Instances are single-owner and are not thread-safe.</remarks>
public sealed class AsyncCleanupRegistry
{
    private readonly Stack<(string Operation, Func<ValueTask> Cleanup)> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Adds a cleanup operation for an acquired resource.
    /// </summary>
    public void Push(string operation, Func<ValueTask> cleanup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(cleanup);
        _entries.Push((operation, cleanup));
    }

    /// <summary>
    /// Runs every pending cleanup operation in reverse order and returns all failures.
    /// </summary>
    public async ValueTask<IReadOnlyList<Exception>> DrainAsync()
    {
        if (_entries.Count == 0)
        {
            return [];
        }

        List<Exception>? failures = null;
        while (_entries.TryPop(out var entry))
        {
            try
            {
                await entry.Cleanup().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures ??= [];
                failures.Add(new InvalidOperationException($"Cleanup operation '{entry.Operation}' failed.", exception));
            }
        }

        return failures is null ? [] : Array.AsReadOnly([.. failures]);
    }
}
