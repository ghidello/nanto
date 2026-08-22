using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Nanto.Hosting;

internal sealed class NantoBridgeProtocolSession : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumMessageBytes = 1024 * 1024;
    internal const int MaximumActiveCommands = 128;
    internal const int MaximumStreams = 32;
    internal const int MaximumEventSubscriptions = 32;
    internal const int EventBufferCapacity = 64;
    internal const int MaximumTraceParentLength = 128;
    internal const int MaximumTraceStateLength = 512;

    private readonly object _gate = new();
    private readonly Dictionary<uint, NantoGeneratedCommand> _commands;
    private readonly Dictionary<uint, NantoGeneratedEvent> _eventDescriptors;
    private readonly HashSet<uint> _capabilities;
    private readonly Dictionary<uint, InvocationState> _active = [];
    private readonly Dictionary<uint, StreamState> _streams = [];
    private readonly Dictionary<uint, EventState> _subscriptions = [];
    private readonly NantoCommandContext _context;
    private readonly string _sessionId;
    private readonly Func<NantoBridgeProtocolSession, string, ValueTask> _output;
    private readonly Action<uint, string, Exception>? _unexpectedFailure;
    private uint _lastRequestId;
    private bool _hasRequestId;
    private bool _ready;
    private bool _closed;

    internal NantoBridgeProtocolSession(
        NantoBridgeConfigurationSnapshot bridge,
        IReadOnlyList<NantoFrontendCapability> capabilities,
        WindowId windowId,
        Uri origin,
        IUiDispatcher dispatcher,
        Func<NantoBridgeProtocolSession, string, ValueTask>? output = null,
        Action<uint, string, Exception>? unexpectedFailure = null)
    {
        _commands = bridge.Commands.ToDictionary(static command => command.Id);
        _eventDescriptors = bridge.Events.ToDictionary(static eventDescriptor => eventDescriptor.Id);
        _capabilities = capabilities.Select(static capability => capability.Id).ToHashSet();
        _context = new(windowId, origin, dispatcher);
        _output = output ?? (static (_, _) => ValueTask.CompletedTask);
        _unexpectedFailure = unexpectedFailure;
        _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        ManifestFingerprint = ComputeManifestFingerprint(bridge);
    }

    internal string ManifestFingerprint { get; }

    internal async ValueTask<string?> HandleAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (message.Length > MaximumMessageBytes)
        {
            return Error(null, "invalidRequest");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return Error(null, "invalidRequest");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type) || !TryUInt32(root, "v", out var version))
            {
                return Error(null, "invalidRequest");
            }

            if (version != ProtocolVersion)
            {
                return Error(TryRequestId(root), "protocolMismatch");
            }

            if (type == "hello")
            {
                return HandleHello(root);
            }

            if (!TryString(root, "session", out var session) || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(session),
                    Encoding.UTF8.GetBytes(_sessionId)))
            {
                return Error(TryRequestId(root), "invalidRequest");
            }

            return type switch
            {
                "invoke" => await InvokeAsync(root, cancellationToken).ConfigureAwait(false),
                "cancel" => await CancelAsync(root).ConfigureAwait(false),
                "streamNext" => await StreamNextAsync(root, cancellationToken).ConfigureAwait(false),
                "subscribe" => Subscribe(root),
                "unsubscribe" => await UnsubscribeAsync(root).ConfigureAwait(false),
                _ => Error(TryRequestId(root), "invalidRequest"),
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        InvocationState[] active;
        StreamState[] streams;
        EventState[] subscriptions;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            active = [.. _active.Values];
            streams = [.. _streams.Values];
            subscriptions = [.. _subscriptions.Values];
            _active.Clear();
            _streams.Clear();
            _subscriptions.Clear();
        }

        List<Exception>? failures = null;
        foreach (var invocation in active)
        {
            try
            {
                invocation.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Handler completion won the teardown race.
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        foreach (var invocation in active)
        {
            await invocation.Completion.ConfigureAwait(false);
        }

        foreach (var subscription in subscriptions)
        {
            try
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        foreach (var stream in streams)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Bridge session cleanup encountered one or more failures.", failures);
        }
    }

    private string HandleHello(JsonElement root)
    {
        if (!TryString(root, "manifest", out var manifest) || !StringComparer.Ordinal.Equals(manifest, ManifestFingerprint))
        {
            return Error(null, "protocolMismatch");
        }

        lock (_gate)
        {
            if (_closed || _ready)
            {
                return Error(null, "invalidRequest");
            }

            _ready = true;
        }

        return Write(writer =>
        {
            writer.WriteString("type", "ready");
            writer.WriteNumber("v", ProtocolVersion);
            writer.WriteString("session", _sessionId);
            writer.WriteString("manifest", ManifestFingerprint);
        });
    }

    private async ValueTask<string> InvokeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!TryUInt32(root, "id", out var requestId) || !TryUInt32(root, "command", out var commandId)
            || !root.TryGetProperty("args", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            return Error(TryRequestId(root), "invalidRequest");
        }

        NantoGeneratedCommand? command;
        InvocationState invocation;
        lock (_gate)
        {
            if (_closed || !_ready || !TryAcceptRequestId(requestId))
            {
                return Error(requestId, "invalidRequest");
            }

            if (!_commands.TryGetValue(commandId, out command) || !_capabilities.Contains(commandId))
            {
                return Error(requestId, "commandUnavailable");
            }

            if (_active.Count >= MaximumActiveCommands || command.Kind == NantoGeneratedCommandKind.Stream && _streams.Count >= MaximumStreams)
            {
                return Error(requestId, "resourceExhausted");
            }

            invocation = new InvocationState(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _active.Add(requestId, invocation);
        }

        var cancellationTransferred = false;
        var outcome = "ok";
        long startedAt = Stopwatch.GetTimestamp();
        ActivityContext parentContext = TryReadTraceContext(root, out var parsedParent) ? parsedParent : default;
        using Activity? activity = NantoTelemetry.ActivitySource.StartActivity(
            "nanto.command.invoke",
            ActivityKind.Server,
            parentContext,
            tags:
            [
                new("nanto.protocol.version", ProtocolVersion),
                new("nanto.command.id", (long)command.Id),
                new("nanto.command.kind", command.Kind == NantoGeneratedCommandKind.Unary ? "unary" : "stream"),
            ]);
        try
        {
            var commandInvocation = await command.InvokeAsync(arguments, _context, invocation.Cancellation.Token).ConfigureAwait(false);
            invocation.Cancellation.Token.ThrowIfCancellationRequested();
            if (commandInvocation is NantoGeneratedUnaryInvocation unary)
            {
                return Result(requestId, unary.Value);
            }

            var stream = new StreamState(((NantoGeneratedStreamInvocation)commandInvocation).Sequence, invocation.Cancellation);
            var discardStream = false;
            lock (_gate)
            {
                if (_closed || invocation.Cancellation.IsCancellationRequested)
                {
                    discardStream = true;
                }
                else
                {
                    _streams.Add(requestId, stream);
                    cancellationTransferred = true;
                }
            }

            if (discardStream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                cancellationTransferred = true;
                return Error(requestId, "cancelled");
            }

            return Write(writer =>
            {
                writer.WriteString("type", "result");
                writer.WriteNumber("id", requestId);
                writer.WriteBoolean("stream", true);
            });
        }
        catch (OperationCanceledException) when (invocation.Cancellation.IsCancellationRequested)
        {
            outcome = "cancelled";
            return Error(requestId, "cancelled");
        }
        catch (NantoGeneratedInvalidRequestException)
        {
            outcome = "invalid_request";
            return Error(requestId, "invalidRequest");
        }
        catch (Exception exception)
        {
            outcome = "internal";
            ReportUnexpectedFailure(requestId, "Invoke", exception);
            return Error(requestId, "internal");
        }
        finally
        {
            TagList metricTags =
            [
                new("nanto.command.id", (long)command.Id),
                new("nanto.command.kind", command.Kind == NantoGeneratedCommandKind.Unary ? "unary" : "stream"),
                new("nanto.outcome", outcome),
            ];
            NantoTelemetry.CommandInvocations.Add(1, metricTags);
            NantoTelemetry.CommandDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, metricTags);
            activity?.SetTag("nanto.outcome", outcome);
            if (outcome != "ok")
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            if (!cancellationTransferred)
            {
                invocation.Cancellation.Dispose();
            }

            invocation.Complete();
            lock (_gate)
            {
                _active.Remove(requestId);
            }
        }
    }

    private bool TryAcceptRequestId(uint requestId)
    {
        if (requestId == 0 || _hasRequestId && requestId <= _lastRequestId)
        {
            return false;
        }

        _lastRequestId = requestId;
        _hasRequestId = true;
        return true;
    }

    private async ValueTask<string?> CancelAsync(JsonElement root)
    {
        if (!TryUInt32(root, "id", out var requestId))
        {
            return Error(TryRequestId(root), "invalidRequest");
        }

        InvocationState? active;
        StreamState? stream;
        lock (_gate)
        {
            _active.TryGetValue(requestId, out active);
            _streams.Remove(requestId, out stream);
        }

        try
        {
            active?.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the cancellation race.
        }
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private async ValueTask<string> StreamNextAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!TryUInt32(root, "id", out var requestId))
        {
            return Error(TryRequestId(root), "invalidRequest");
        }

        StreamState? stream;
        lock (_gate)
        {
            _streams.TryGetValue(requestId, out stream);
        }

        if (stream is null)
        {
            return Error(requestId, "invalidRequest");
        }

        try
        {
            var item = await stream.Sequence.MoveNextAsync(cancellationToken).ConfigureAwait(false);
            if (item.HasValue)
            {
                var response = Write(writer =>
                {
                    writer.WriteString("type", "item");
                    writer.WriteNumber("id", requestId);
                    writer.WritePropertyName("value");
                    item.Value.WriteTo(writer);
                });
                if (IsWithinMessageLimit(response))
                {
                    return response;
                }

                lock (_gate)
                {
                    _streams.Remove(requestId);
                }

                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    ReportUnexpectedFailure(requestId, "StreamDispose", cleanupException);
                }

                return Error(requestId, "resourceExhausted");
            }

            lock (_gate)
            {
                _streams.Remove(requestId);
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            return Write(writer =>
            {
                writer.WriteString("type", "completion");
                writer.WriteNumber("id", requestId);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || stream.IsCancellationRequested)
        {
            lock (_gate)
            {
                _streams.Remove(requestId);
            }

            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                ReportUnexpectedFailure(requestId, "StreamDispose", cleanupException);
            }

            return Error(requestId, "cancelled");
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _streams.Remove(requestId);
            }

            ReportUnexpectedFailure(requestId, "StreamNext", exception);
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                ReportUnexpectedFailure(requestId, "StreamDispose", cleanupException);
            }

            return Error(requestId, "internal");
        }
    }

    private void ReportUnexpectedFailure(uint requestId, string operation, Exception exception)
    {
        try
        {
            _unexpectedFailure?.Invoke(requestId, operation, exception);
        }
        catch
        {
            // Failure reporting must not replace the sanitized protocol response.
        }
    }

    private string Subscribe(JsonElement root)
    {
        if (!TryUInt32(root, "id", out var subscriptionId) || !TryUInt32(root, "event", out var eventId))
        {
            return Error(TryRequestId(root), "invalidRequest");
        }

        lock (_gate)
        {
            if (_closed || !_ready || !TryAcceptRequestId(subscriptionId) || _subscriptions.ContainsKey(subscriptionId))
            {
                return Error(subscriptionId, "invalidRequest");
            }

            var eventDescriptor = _eventDescriptors.GetValueOrDefault(eventId);
            if (eventDescriptor is null || !_capabilities.Contains(eventId))
            {
                return Error(subscriptionId, "commandUnavailable");
            }

            if (_subscriptions.Count >= MaximumEventSubscriptions)
            {
                return Error(subscriptionId, "resourceExhausted");
            }

            var state = new EventState(this, subscriptionId, eventDescriptor, _output);
            _subscriptions.Add(subscriptionId, state);
            state.Start();
        }

        return Write(writer =>
        {
            writer.WriteString("type", "result");
            writer.WriteNumber("id", subscriptionId);
        });
    }

    private async ValueTask<string?> UnsubscribeAsync(JsonElement root)
    {
        if (!TryUInt32(root, "id", out var subscriptionId))
        {
            return Error(TryRequestId(root), "invalidRequest");
        }

        EventState? state;
        lock (_gate)
        {
            _subscriptions.Remove(subscriptionId, out state);
        }

        if (state is not null)
        {
            await state.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private void RemoveTerminatedSubscription(uint id, EventState state)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(id, out var current) && ReferenceEquals(current, state))
            {
                _subscriptions.Remove(id);
            }
        }
    }

    private static string ComputeManifestFingerprint(NantoBridgeConfigurationSnapshot bridge)
    {
        var manifest = string.Join("\n", bridge.ManifestEntries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v1\n" + manifest))).ToLowerInvariant();
    }

    private static string Result(uint requestId, JsonElement value)
    {
        var response = Write(writer =>
        {
            writer.WriteString("type", "result");
            writer.WriteNumber("id", requestId);
            writer.WritePropertyName("value");
            value.WriteTo(writer);
        });
        return IsWithinMessageLimit(response) ? response : Error(requestId, "resourceExhausted");
    }

    private static string Error(uint? requestId, string code) => Write(writer =>
    {
        writer.WriteString("type", "error");
        if (requestId.HasValue)
        {
            writer.WriteNumber("id", requestId.Value);
        }

        writer.WriteString("code", code);
    });

    private static string Write(Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsWithinMessageLimit(string response) => Encoding.UTF8.GetByteCount(response) <= MaximumMessageBytes;

    private static uint? TryRequestId(JsonElement root) => TryUInt32(root, "id", out var id) ? id : null;

    private static bool TryUInt32(JsonElement root, string name, out uint value)
    {
        value = default;
        return root.TryGetProperty(name, out var property) && property.TryGetUInt32(out value);
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static bool TryReadTraceContext(JsonElement root, out ActivityContext context)
    {
        context = default;
        if (!root.TryGetProperty("traceparent", out var traceParentProperty))
        {
            return false;
        }

        if (traceParentProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string traceParent = traceParentProperty.GetString()!;
        if (traceParent.Length > MaximumTraceParentLength)
        {
            return false;
        }

        string? traceState = null;
        if (root.TryGetProperty("tracestate", out var traceStateProperty))
        {
            if (traceStateProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            traceState = traceStateProperty.GetString();
            if (!IsValidTraceState(traceState))
            {
                return false;
            }
        }

        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
    }

    private static bool IsValidTraceState(string? traceState)
    {
        if (traceState is null || traceState.Length == 0 || traceState.Length > MaximumTraceStateLength || !traceState.All(static character => character <= 0x7f))
        {
            return false;
        }

        string[] members = traceState.Split(',');
        if (members.Length > 32)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string untrimmedMember in members)
        {
            string member = untrimmedMember.Trim(' ', '\t');
            int equals = member.IndexOf('=');
            if (equals <= 0 || equals == member.Length - 1)
            {
                return false;
            }

            string key = member[..equals];
            string value = member[(equals + 1)..];
            if (!keys.Add(key) || !IsValidTraceStateKey(key) || !IsValidTraceStateValue(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidTraceStateKey(string key)
    {
        int at = key.IndexOf('@');
        if (at < 0)
        {
            return key.Length <= 256 && IsLowerAlpha(key[0]) && key.All(IsTraceStateKeyCharacter);
        }

        string tenant = key[..at];
        string system = key[(at + 1)..];
        return key.LastIndexOf('@') == at
            && tenant.Length is > 0 and <= 241
            && system.Length is > 0 and <= 14
            && IsLowerAlphaNumeric(tenant[0])
            && tenant.All(IsTraceStateKeyCharacter)
            && IsLowerAlpha(system[0])
            && system.All(IsTraceStateKeyCharacter);
    }

    private static bool IsValidTraceStateValue(string value) => value.Length <= 256
        && value[0] != ' '
        && value[^1] != ' '
        && value.All(static character => character is >= (char)0x20 and <= (char)0x7e && character is not ',' and not '=');

    private static bool IsTraceStateKeyCharacter(char character) => IsLowerAlphaNumeric(character) || character is '_' or '-' or '*' or '/';

    private static bool IsLowerAlpha(char character) => character is >= 'a' and <= 'z';

    private static bool IsLowerAlphaNumeric(char character) => IsLowerAlpha(character) || character is >= '0' and <= '9';

    private sealed class StreamState(NantoGeneratedSequence sequence, CancellationTokenSource cancellation) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposal;

        internal NantoGeneratedSequence Sequence { get; } = sequence;

        internal bool IsCancellationRequested => cancellation.IsCancellationRequested;

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposal ??= DisposeCoreAsync();
                return new ValueTask(_disposal);
            }
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                cancellation.Cancel();
                await Sequence.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed class InvocationState(CancellationTokenSource cancellation)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenSource Cancellation { get; } = cancellation;

        internal Task Completion => _completion.Task;

        internal void Complete() => _completion.TrySetResult();
    }

    private sealed class EventState : IAsyncDisposable
    {
        private readonly uint _id;
        private readonly NantoBridgeProtocolSession _session;
        private readonly Func<NantoBridgeProtocolSession, string, ValueTask> _output;
        private readonly Channel<JsonElement> _items = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(EventBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly NantoGeneratedEventSubscription _source;
        private Task? _pump;
        private int _overflowed;
        private int _disposed;

        internal EventState(
            NantoBridgeProtocolSession session,
            uint id,
            NantoGeneratedEvent descriptor,
            Func<NantoBridgeProtocolSession, string, ValueTask> output)
        {
            _session = session;
            _id = id;
            _output = output;
            _source = descriptor.Subscribe(PublishAsync);
        }

        internal void Start() => _pump = PumpAsync();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _items.Writer.TryComplete();
            await _source.DisposeAsync().ConfigureAwait(false);
            if (_pump is not null)
            {
                await _pump.ConfigureAwait(false);
            }
        }

        private ValueTask PublishAsync(JsonElement item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_items.Writer.TryWrite(item))
            {
                Interlocked.Exchange(ref _overflowed, 1);
                _items.Writer.TryComplete();
            }

            return ValueTask.CompletedTask;
        }

        private async Task PumpAsync()
        {
            await foreach (var item in _items.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var response = Write(writer =>
                {
                    writer.WriteString("type", "item");
                    writer.WriteNumber("id", _id);
                    writer.WritePropertyName("value");
                    item.WriteTo(writer);
                });
                if (!IsWithinMessageLimit(response))
                {
                    Interlocked.Exchange(ref _overflowed, 1);
                    _items.Writer.TryComplete();
                    break;
                }

                await _output(_session, response).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _overflowed) != 0)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
                _session.RemoveTerminatedSubscription(_id, this);
                await _output(_session, Error(_id, "resourceExhausted")).ConfigureAwait(false);
            }
            else if (Volatile.Read(ref _disposed) == 0)
            {
                await _output(_session, Write(writer =>
                {
                    writer.WriteString("type", "completion");
                    writer.WriteNumber("id", _id);
                })).ConfigureAwait(false);
            }
        }
    }
}
