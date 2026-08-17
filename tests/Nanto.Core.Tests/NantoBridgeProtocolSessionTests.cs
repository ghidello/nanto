using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Nanto.Hosting;

namespace Nanto.Core.Tests;

public sealed class NantoBridgeProtocolSessionTests
{
    [Fact]
    public async Task NullableResultsAndEventsPreserveNull()
    {
        var result = NantoResult.Success<string?, int>(null);
        var source = new NantoEvent<string?>();

        await source.PublishAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task InvokesAuthorizedCommandAfterExactManifestHandshake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(42, static (_, _, _) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
            new NantoGeneratedUnaryInvocation(JsonSerializer.SerializeToElement(17))));
        await using var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);

        var response = await session.HandleAsync(Invoke(sessionId, 7, 42), cancellationToken);

        using var document = JsonDocument.Parse(response!);
        Assert.Equal("result", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(17, document.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RejectsUnaryResultsThatExceedTheMessageLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(42, static (_, _, _) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
            new NantoGeneratedUnaryInvocation(JsonSerializer.SerializeToElement(new string('x', NantoBridgeProtocolSession.MaximumMessageBytes)))));
        await using var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);

        var response = await session.HandleAsync(Invoke(sessionId, 1, 42), cancellationToken);

        Assert.Equal("resourceExhausted", ReadCode(response));
    }

    [Fact]
    public async Task UnknownAndUnauthorizedCommandsAreIndistinguishable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(42, static (_, _, _) => throw new InvalidOperationException());
        await using var unauthorized = CreateSession(command, grant: false);
        var unauthorizedSession = await HandshakeAsync(unauthorized, cancellationToken);
        await using var unknown = CreateSession(command, grant: true);
        var unknownSession = await HandshakeAsync(unknown, cancellationToken);

        var unauthorizedResponse = await unauthorized.HandleAsync(Invoke(unauthorizedSession, 1, 42), cancellationToken);
        var unknownResponse = await unknown.HandleAsync(Invoke(unknownSession, 1, 99), cancellationToken);

        Assert.Equal("commandUnavailable", ReadCode(unauthorizedResponse));
        Assert.Equal("commandUnavailable", ReadCode(unknownResponse));
    }

    [Fact]
    public async Task RejectsOversizedAndDuplicateRequests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(42, static (_, _, _) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
            new NantoGeneratedUnaryInvocation(NantoGeneratedJson.Null)));
        await using var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);
        var request = Invoke(sessionId, 1, 42);

        _ = await session.HandleAsync(request, cancellationToken);
        var duplicate = await session.HandleAsync(request, cancellationToken);
        var oversized = await session.HandleAsync(new byte[NantoBridgeProtocolSession.MaximumMessageBytes + 1], cancellationToken);

        Assert.Equal("invalidRequest", ReadCode(duplicate));
        Assert.Equal("invalidRequest", ReadCode(oversized));
    }

    [Fact]
    public void BridgeSnapshotDoesNotObserveLaterRegistrationListMutations()
    {
        var commands = new List<NantoGeneratedCommand>
        {
            new TestCommand(42, static (_, _, _) => throw new InvalidOperationException()),
        };
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration(commands, []));

        var snapshot = bridge.CaptureSnapshot();
        commands.Add(new TestCommand(43, static (_, _, _) => throw new InvalidOperationException()));

        Assert.Single(snapshot.Commands);
        Assert.Equal(42U, snapshot.Commands[0].Id);
    }

    [Fact]
    public async Task PublishesHotEventsOnlyAfterSubscription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = new NantoEvent<int>();
        var eventDescriptor = new NantoGeneratedEvent<int>(
            77,
            "projects.changed",
            "event:projects.changed->int|schemas:int=scalar",
            source,
            static value => JsonSerializer.SerializeToElement(value));
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([], [eventDescriptor]));
        var output = Channel.CreateUnbounded<string>();
        await using var session = new NantoBridgeProtocolSession(
            bridge.CaptureSnapshot(),
            [new NantoFrontendCapability(77, "projects.changed", NantoFrontendCapabilityKind.Event)],
            WindowId.Create(),
            new Uri("https://app.nanto.invalid"),
            new InlineDispatcher(),
            (_, message) => output.Writer.WriteAsync(message, cancellationToken));
        var sessionId = await HandshakeAsync(session, cancellationToken);
        await source.PublishAsync(1, cancellationToken);

        var subscribed = await session.HandleAsync(
            Utf8(JsonSerializer.Serialize(new { v = 1, type = "subscribe", session = sessionId, id = 9, @event = 77 })),
            cancellationToken);
        await source.PublishAsync(2, cancellationToken);
        var item = await output.Reader.ReadAsync(cancellationToken);

        Assert.Equal("result", JsonDocument.Parse(subscribed!).RootElement.GetProperty("type").GetString());
        using var document = JsonDocument.Parse(item);
        Assert.Equal(2, document.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task PublishesAnEventToEveryActiveSubscription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = new NantoEvent<int>();
        var eventDescriptor = new NantoGeneratedEvent<int>(
            77,
            "projects.changed",
            "event:projects.changed->int|schemas:int=scalar",
            source,
            static value => JsonSerializer.SerializeToElement(value));
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([], [eventDescriptor]));
        var output = Channel.CreateUnbounded<string>();
        await using var session = new NantoBridgeProtocolSession(
            bridge.CaptureSnapshot(),
            [new NantoFrontendCapability(77, "projects.changed", NantoFrontendCapabilityKind.Event)],
            WindowId.Create(),
            new Uri("https://app.nanto.invalid"),
            new InlineDispatcher(),
            (_, message) => output.Writer.WriteAsync(message, cancellationToken));
        var sessionId = await HandshakeAsync(session, cancellationToken);

        _ = await session.HandleAsync(
            Utf8(JsonSerializer.Serialize(new { v = 1, type = "subscribe", session = sessionId, id = 9, @event = 77 })),
            cancellationToken);
        _ = await session.HandleAsync(
            Utf8(JsonSerializer.Serialize(new { v = 1, type = "subscribe", session = sessionId, id = 10, @event = 77 })),
            cancellationToken);
        await source.PublishAsync(2, cancellationToken);

        var first = JsonDocument.Parse(await output.Reader.ReadAsync(cancellationToken));
        var second = JsonDocument.Parse(await output.Reader.ReadAsync(cancellationToken));
        using (first)
        using (second)
        {
            Assert.Equal([9U, 10U], new[] { first.RootElement.GetProperty("id").GetUInt32(), second.RootElement.GetProperty("id").GetUInt32() }.Order());
        }
    }

    [Fact]
    public async Task RejectsStreamsBeyondThePerWindowLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(
            42,
            static (_, _, _) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
                new NantoGeneratedStreamInvocation(new EmptySequence())),
            kind: NantoGeneratedCommandKind.Stream);
        await using var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);
        for (uint requestId = 1; requestId <= NantoBridgeProtocolSession.MaximumStreams; requestId++)
        {
            var accepted = await session.HandleAsync(Invoke(sessionId, requestId, 42), cancellationToken);
            Assert.Equal("result", JsonDocument.Parse(accepted!).RootElement.GetProperty("type").GetString());
        }

        var rejected = await session.HandleAsync(
            Invoke(sessionId, NantoBridgeProtocolSession.MaximumStreams + 1U, 42),
            cancellationToken);

        Assert.Equal("resourceExhausted", ReadCode(rejected));
    }

    [Fact]
    public async Task RejectsCommandsBeyondThePerWindowLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var command = new TestCommand(42, async (_, _, invocationCancellation) =>
        {
            if (Interlocked.Increment(ref started) == NantoBridgeProtocolSession.MaximumActiveCommands)
            {
                allStarted.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, invocationCancellation).ConfigureAwait(false);
            return new NantoGeneratedUnaryInvocation(NantoGeneratedJson.Null);
        });
        var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);
        var active = new List<Task<string?>>();
        for (uint requestId = 1; requestId <= NantoBridgeProtocolSession.MaximumActiveCommands; requestId++)
        {
            active.Add(session.HandleAsync(Invoke(sessionId, requestId, 42), cancellationToken).AsTask());
        }

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var rejected = await session.HandleAsync(
            Invoke(sessionId, NantoBridgeProtocolSession.MaximumActiveCommands + 1U, 42),
            cancellationToken);

        Assert.Equal("resourceExhausted", ReadCode(rejected));
        await session.DisposeAsync();
        var completions = await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.All(completions, response => Assert.Equal("cancelled", ReadCode(response)));
    }

    [Fact]
    public async Task RejectsEventSubscriptionsBeyondThePerWindowLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = new NantoEvent<int>();
        var eventDescriptor = CreateEventDescriptor(source);
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([], [eventDescriptor]));
        await using var session = new NantoBridgeProtocolSession(
            bridge.CaptureSnapshot(),
            [new NantoFrontendCapability(77, "projects.changed", NantoFrontendCapabilityKind.Event)],
            WindowId.Create(),
            new Uri("https://app.nanto.invalid"),
            new InlineDispatcher());
        var sessionId = await HandshakeAsync(session, cancellationToken);
        for (uint subscriptionId = 1; subscriptionId <= NantoBridgeProtocolSession.MaximumEventSubscriptions; subscriptionId++)
        {
            var accepted = await session.HandleAsync(Subscribe(sessionId, subscriptionId), cancellationToken);
            Assert.Equal("result", JsonDocument.Parse(accepted!).RootElement.GetProperty("type").GetString());
        }

        var rejected = await session.HandleAsync(
            Subscribe(sessionId, NantoBridgeProtocolSession.MaximumEventSubscriptions + 1U),
            cancellationToken);

        Assert.Equal("resourceExhausted", ReadCode(rejected));
    }

    [Fact]
    public async Task EventBufferOverflowTerminatesOnlyTheSlowSubscription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = new NantoEvent<int>();
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([], [CreateEventDescriptor(source)]));
        var output = Channel.CreateUnbounded<string>();
        var releaseFirstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputCount = 0;
        await using var session = new NantoBridgeProtocolSession(
            bridge.CaptureSnapshot(),
            [new NantoFrontendCapability(77, "projects.changed", NantoFrontendCapabilityKind.Event)],
            WindowId.Create(),
            new Uri("https://app.nanto.invalid"),
            new InlineDispatcher(),
            async (_, message) =>
            {
                output.Writer.TryWrite(message);
                if (Interlocked.Increment(ref outputCount) == 1)
                {
                    await releaseFirstOutput.Task.ConfigureAwait(false);
                }
            });
        var sessionId = await HandshakeAsync(session, cancellationToken);
        _ = await session.HandleAsync(Subscribe(sessionId, 1), cancellationToken);
        for (var item = 0; item < NantoBridgeProtocolSession.EventBufferCapacity + 2; item++)
        {
            await source.PublishAsync(item, cancellationToken);
        }

        releaseFirstOutput.TrySetResult();
        string? terminal = null;
        while (terminal is null)
        {
            var message = await output.Reader.ReadAsync(cancellationToken);
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.GetProperty("type").GetString() == "error")
            {
                terminal = document.RootElement.GetProperty("code").GetString();
            }
        }

        Assert.Equal("resourceExhausted", terminal);
        var secondSubscription = await session.HandleAsync(Subscribe(sessionId, 2), cancellationToken);
        Assert.Equal("result", JsonDocument.Parse(secondSubscription!).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ManifestChangesWhenOnlyDtoSchemaChanges()
    {
        var first = new TestCommand(
            42,
            static (_, _, _) => throw new InvalidOperationException(),
            manifestEntry: "command:projects.open()->Project|schemas:Project=object(Id:int)");
        var second = new TestCommand(
            42,
            static (_, _, _) => throw new InvalidOperationException(),
            manifestEntry: "command:projects.open()->Project|schemas:Project=object(Id:int,Name:string)");
        await using var firstSession = CreateSession(first, grant: true);
        await using var secondSession = CreateSession(second, grant: true);

        Assert.NotEqual(firstSession.ManifestFingerprint, secondSession.ManifestFingerprint);
    }

    [Fact]
    public async Task GeneratedArgumentFailuresReturnInvalidRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(42, static (_, _, _) => throw new NantoGeneratedInvalidRequestException(new JsonException()));
        await using var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);

        var response = await session.HandleAsync(Invoke(sessionId, 1, 42), cancellationToken);

        Assert.Equal("invalidRequest", ReadCode(response));
    }

    [Fact]
    public async Task CancellingBlockedStreamCancelsMoveBeforeDisposal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(
            42,
            static (_, _, invocationCancellation) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
                new NantoGeneratedStreamInvocation(new NantoGeneratedSequence<int>(
                    BlockForever(invocationCancellation),
                    static value => JsonSerializer.SerializeToElement(value),
                    invocationCancellation))),
            kind: NantoGeneratedCommandKind.Stream);
        await using var session = CreateSession(command, grant: true);
        var sessionId = await HandshakeAsync(session, cancellationToken);
        _ = await session.HandleAsync(Invoke(sessionId, 1, 42), cancellationToken);
        var next = session.HandleAsync(
            Utf8(JsonSerializer.Serialize(new { v = 1, type = "streamNext", session = sessionId, id = 1 })),
            cancellationToken).AsTask();

        var cancel = session.HandleAsync(
            Utf8(JsonSerializer.Serialize(new { v = 1, type = "cancel", session = sessionId, id = 1 })),
            cancellationToken).AsTask();

        var responses = await Task.WhenAll(next, cancel).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal("cancelled", ReadCode(responses[0]));
    }

    [Fact]
    public async Task StreamFailureRemainsSanitizedWhenDisposalAlsoFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var command = new TestCommand(
            42,
            static (_, _, _) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
                new NantoGeneratedStreamInvocation(new FailingSequence())),
            kind: NantoGeneratedCommandKind.Stream);
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([command], []));
        var failures = new List<(uint RequestId, string Operation)>();
        await using var session = new NantoBridgeProtocolSession(
            bridge.CaptureSnapshot(),
            [new NantoFrontendCapability(command.Id, command.SymbolicName, NantoFrontendCapabilityKind.Command)],
            WindowId.Create(),
            new Uri("https://app.nanto.invalid"),
            new InlineDispatcher(),
            unexpectedFailure: (requestId, operation, _) => failures.Add((requestId, operation)));
        var sessionId = await HandshakeAsync(session, cancellationToken);
        _ = await session.HandleAsync(Invoke(sessionId, 1, 42), cancellationToken);

        var response = await session.HandleAsync(
            Utf8(JsonSerializer.Serialize(new { v = 1, type = "streamNext", session = sessionId, id = 1 })),
            cancellationToken);

        Assert.Equal("internal", ReadCode(response));
        Assert.Equal([(1U, "StreamNext"), (1U, "StreamDispose")], failures);
    }

    private static async IAsyncEnumerable<int> BlockForever([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static NantoBridgeProtocolSession CreateSession(TestCommand command, bool grant)
    {
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([command], []));
        var capabilities = grant ? new[] { new NantoFrontendCapability(command.Id, command.SymbolicName, NantoFrontendCapabilityKind.Command) } : [];
        return new(bridge.CaptureSnapshot(), capabilities, WindowId.Create(), new Uri("https://app.nanto.invalid"), new InlineDispatcher());
    }

    private static async Task<string> HandshakeAsync(NantoBridgeProtocolSession session, CancellationToken cancellationToken)
    {
        var response = await session.HandleAsync(Utf8($$"""{"v":1,"type":"hello","manifest":"{{session.ManifestFingerprint}}"}"""), cancellationToken);
        using var document = JsonDocument.Parse(response!);
        return document.RootElement.GetProperty("session").GetString()!;
    }

    private static string ReadCode(string? response)
    {
        using var document = JsonDocument.Parse(response!);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static ReadOnlyMemory<byte> Invoke(string session, uint id, uint command) =>
        Utf8(JsonSerializer.Serialize(new { v = 1, type = "invoke", session, id, command, args = new { } }));

    private static ReadOnlyMemory<byte> Subscribe(string session, uint id) =>
        Utf8(JsonSerializer.Serialize(new { v = 1, type = "subscribe", session, id, @event = 77 }));

    private static NantoGeneratedEvent<int> CreateEventDescriptor(NantoEvent<int> source) => new(
        77,
        "projects.changed",
        "event:projects.changed->int|schemas:int=scalar",
        source,
        static value => JsonSerializer.SerializeToElement(value));

    private sealed class TestRegistration(
        IReadOnlyList<NantoGeneratedCommand> commands,
        IReadOnlyList<NantoGeneratedEvent> events) : NantoGeneratedApiRegistration
    {
        public override IReadOnlyList<NantoGeneratedCommand> Commands { get; } = commands;

        public override IReadOnlyList<NantoGeneratedEvent> Events { get; } = events;
    }

    private sealed class TestCommand(
        uint id,
        Func<JsonElement, NantoCommandContext, CancellationToken, ValueTask<NantoGeneratedCommandInvocation>> invoke,
        string manifestEntry = "command:projects.open()->int|schemas:int=scalar",
        NantoGeneratedCommandKind kind = NantoGeneratedCommandKind.Unary) : NantoGeneratedCommand
    {
        public override uint Id { get; } = id;

        public override string SymbolicName { get; } = "projects.open";

        public override string ManifestEntry { get; } = manifestEntry;

        public override NantoGeneratedCommandKind Kind { get; } = kind;

        public override ValueTask<NantoGeneratedCommandInvocation> InvokeAsync(
            JsonElement arguments,
            NantoCommandContext context,
            CancellationToken cancellationToken) => invoke(arguments, context, cancellationToken);
    }

    private sealed class FailingSequence : NantoGeneratedSequence
    {
        public override ValueTask<NantoGeneratedStreamItem> MoveNextAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<NantoGeneratedStreamItem>(new InvalidOperationException("move failed"));

        public override ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("dispose failed"));
    }

    private sealed class EmptySequence : NantoGeneratedSequence
    {
        public override ValueTask<NantoGeneratedStreamItem> MoveNextAsync(CancellationToken cancellationToken) => ValueTask.FromResult(default(NantoGeneratedStreamItem));

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return ValueTask.CompletedTask;
        }

        public ValueTask<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) => ValueTask.FromResult(action());

        public ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default) => action(cancellationToken);

        public ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default) => action(cancellationToken);
    }
}
