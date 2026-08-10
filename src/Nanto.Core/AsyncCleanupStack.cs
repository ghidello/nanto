namespace Nanto;

internal sealed class AsyncCleanupStack
{
    private readonly Stack<(string Operation, Func<ValueTask> Cleanup)> _entries = new();

    public int Count => _entries.Count;

    public void Push(string operation, Func<ValueTask> cleanup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(cleanup);
        _entries.Push((operation, cleanup));
    }

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