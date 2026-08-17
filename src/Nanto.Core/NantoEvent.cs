namespace Nanto;

/// <summary>Publishes a typed, hot, non-replayed event to currently subscribed frontend windows.</summary>
public sealed class NantoEvent<T>
{
    private readonly object _gate = new();
    private readonly List<NantoEventPublisher<T>> _publishers = [];

    public async ValueTask PublishAsync(T value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NantoEventPublisher<T>[] publishers;
        lock (_gate)
        {
            publishers = [.. _publishers];
        }

        foreach (var publisher in publishers)
        {
            await publisher(value, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void Attach(NantoEventPublisher<T> publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        lock (_gate)
        {
            _publishers.Add(publisher);
        }
    }

    internal void Detach(NantoEventPublisher<T> publisher)
    {
        lock (_gate)
        {
            _publishers.Remove(publisher);
        }
    }
}

internal delegate ValueTask NantoEventPublisher<in T>(T value, CancellationToken cancellationToken);
