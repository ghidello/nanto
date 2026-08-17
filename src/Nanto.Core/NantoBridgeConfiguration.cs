using System.ComponentModel;
using System.Text.Json;

namespace Nanto;

/// <summary>Collects explicitly generated frontend bridge registrations.</summary>
public sealed class NantoBridgeConfiguration
{
    private readonly object _gate = new();
    private readonly List<NantoGeneratedApiRegistration> _registrations = [];

    [EditorBrowsable(EditorBrowsableState.Never)]
    public NantoBridgeConfiguration AddGenerated(NantoGeneratedApiRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            _registrations.Add(registration);
        }

        return this;
    }

    internal NantoBridgeConfigurationSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new NantoBridgeConfigurationSnapshot(
                [.. _registrations.SelectMany(static registration => registration.Commands)],
                [.. _registrations.SelectMany(static registration => registration.Events)]);
        }
    }
}

/// <summary>Base class implemented by generated, reflection-free API registrations.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NantoGeneratedApiRegistration
{
    public abstract IReadOnlyList<NantoGeneratedCommand> Commands { get; }

    public abstract IReadOnlyList<NantoGeneratedEvent> Events { get; }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NantoGeneratedCommand
{
    public abstract uint Id { get; }

    public abstract string SymbolicName { get; }

    public abstract string ManifestEntry { get; }

    public abstract NantoGeneratedCommandKind Kind { get; }

    public abstract ValueTask<NantoGeneratedCommandInvocation> InvokeAsync(
        JsonElement arguments,
        NantoCommandContext context,
        CancellationToken cancellationToken);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NantoGeneratedInvalidRequestException(Exception innerException) : Exception(
    "The generated command arguments are invalid.",
    innerException);

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NantoGeneratedEvent
{
    public abstract uint Id { get; }

    public abstract string SymbolicName { get; }

    public abstract string ManifestEntry { get; }

    public abstract NantoGeneratedEventSubscription Subscribe(Func<JsonElement, CancellationToken, ValueTask> publish);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NantoGeneratedEventSubscription : IAsyncDisposable
{
    public abstract ValueTask DisposeAsync();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NantoGeneratedEvent<T>(
    uint id,
    string symbolicName,
    string manifestEntry,
    NantoEvent<T> source,
    Func<T, JsonElement> serialize) : NantoGeneratedEvent
{
    public override uint Id { get; } = id;

    public override string SymbolicName { get; } = symbolicName;

    public override string ManifestEntry { get; } = manifestEntry;

    public override NantoGeneratedEventSubscription Subscribe(Func<JsonElement, CancellationToken, ValueTask> publish) =>
        new Subscription(source, serialize, publish);

    private sealed class Subscription : NantoGeneratedEventSubscription
    {
        private readonly NantoEvent<T> _source;
        private readonly NantoEventPublisher<T> _publisher;
        private int _disposed;

        public Subscription(NantoEvent<T> source, Func<T, JsonElement> serialize, Func<JsonElement, CancellationToken, ValueTask> publish)
        {
            _source = source;
            _publisher = (value, cancellationToken) => publish(serialize(value), cancellationToken);
            source.Attach(_publisher);
        }

        public override ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _source.Detach(_publisher);
            }

            return ValueTask.CompletedTask;
        }
    }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public enum NantoGeneratedCommandKind
{
    Unary,
    Stream,
}

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NantoGeneratedCommandInvocation;

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NantoGeneratedUnaryInvocation(JsonElement value) : NantoGeneratedCommandInvocation
{
    public JsonElement Value { get; } = value;
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NantoGeneratedStreamInvocation(NantoGeneratedSequence sequence) : NantoGeneratedCommandInvocation
{
    public NantoGeneratedSequence Sequence { get; } = sequence;
}

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class NantoGeneratedSequence : IAsyncDisposable
{
    public abstract ValueTask<NantoGeneratedStreamItem> MoveNextAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public readonly record struct NantoGeneratedStreamItem(bool HasValue, JsonElement Value);

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NantoGeneratedSequence<T>(
    IAsyncEnumerable<T> source,
    Func<T, JsonElement> serialize,
    CancellationToken cancellationToken) : NantoGeneratedSequence
{
    private readonly IAsyncEnumerator<T> _enumerator = source.GetAsyncEnumerator(cancellationToken);
    private readonly Func<T, JsonElement> _serialize = serialize;
    private readonly SemaphoreSlim _moveGate = new(1, 1);
    private int _disposed;

    public override async ValueTask<NantoGeneratedStreamItem> MoveNextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _moveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                return default;
            }

            return new(true, _serialize(_enumerator.Current));
        }
        finally
        {
            _moveGate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _moveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _enumerator.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _moveGate.Release();
            }
        }
    }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public static class NantoGeneratedJson
{
    private static readonly JsonElement _null = CreateNull();

    public static JsonElement Null => _null;

    public static JsonElement CreateResult(bool succeeded, JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", succeeded);
            writer.WritePropertyName(succeeded ? "value" : "error");
            value.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement CreateNull()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}

internal sealed class NantoBridgeConfigurationSnapshot(
    IReadOnlyList<NantoGeneratedCommand> commands,
    IReadOnlyList<NantoGeneratedEvent> events)
{
    public IReadOnlyList<NantoGeneratedCommand> Commands { get; } = commands;

    public IReadOnlyList<NantoGeneratedEvent> Events { get; } = events;
}
