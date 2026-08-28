using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;

using Nanto.Hosting;

namespace Nanto.Core.Tests;

public sealed class NantoTelemetryTests
{
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public async Task ValidRemoteContextParentsBoundedServerActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == NantoTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        Activity? commandActivity = null;
        await using var session = CreateSession((_, _, _) =>
        {
            commandActivity = Activity.Current;
            return ValueTask.FromResult<NantoGeneratedCommandInvocation>(new NantoGeneratedUnaryInvocation(JsonSerializer.SerializeToElement(17)));
        });
        string sessionId = await HandshakeAsync(session);

        string? response = await session.HandleAsync(Invoke(sessionId, TraceParent, "vendor=value"), TestContext.Current.CancellationToken);

        Assert.Contains("\"value\":17", response, StringComparison.Ordinal);
        Assert.NotNull(commandActivity);
        Assert.Equal(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), commandActivity.TraceId);
        Assert.Equal(ActivitySpanId.CreateFromString("00f067aa0ba902b7"), commandActivity.ParentSpanId);
        Assert.Equal(ActivityKind.Server, commandActivity.Kind);
        Assert.Equal("ok", commandActivity.GetTagItem("nanto.outcome"));
        Assert.DoesNotContain(commandActivity.TagObjects, static tag => tag.Value?.ToString()?.Contains("app.nanto.invalid", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task MalformedTraceStateIsIgnoredInsteadOfPropagated()
    {
        Activity? commandActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == NantoTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        await using var session = CreateSession((_, _, _) =>
        {
            commandActivity = Activity.Current;
            return ValueTask.FromResult<NantoGeneratedCommandInvocation>(new NantoGeneratedUnaryInvocation(JsonSerializer.SerializeToElement(true)));
        });
        string sessionId = await HandshakeAsync(session);

        await session.HandleAsync(Invoke(sessionId, TraceParent, "vendor=one,vendor=two"), TestContext.Current.CancellationToken);

        Assert.NotNull(commandActivity);
        Assert.Equal(default, commandActivity.ParentSpanId);
        Assert.Null(commandActivity.TraceStateString);
    }

    [Fact]
    public async Task CommandMetricsUseOnlyBoundedOutcomeAndDescriptorTags()
    {
        long invocationCount = 0;
        KeyValuePair<string, object?>[]? observedTags = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NantoTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "nanto.command.invocations")
            {
                return;
            }

            KeyValuePair<string, object?>[] candidate = tags.ToArray();
            if (!candidate.Any(static tag => tag.Key == "nanto.command.id" && Equals(tag.Value, 42L))
                || !candidate.Any(static tag => tag.Key == "nanto.command.kind" && Equals(tag.Value, "unary"))
                || !candidate.Any(static tag => tag.Key == "nanto.outcome" && Equals(tag.Value, "ok")))
            {
                return;
            }

            Interlocked.Add(ref invocationCount, measurement);
            observedTags = candidate;
        });
        listener.Start();
        await using var session = CreateSession(static (_, _, _) => ValueTask.FromResult<NantoGeneratedCommandInvocation>(
            new NantoGeneratedUnaryInvocation(JsonSerializer.SerializeToElement(true))));
        string sessionId = await HandshakeAsync(session);

        await session.HandleAsync(Invoke(sessionId), TestContext.Current.CancellationToken);

        Assert.True(invocationCount >= 1);
        Assert.NotNull(observedTags);
        Assert.Contains(observedTags, static tag => tag.Key == "nanto.command.id" && Equals(tag.Value, 42L));
        Assert.Contains(observedTags, static tag => tag.Key == "nanto.command.kind" && Equals(tag.Value, "unary"));
        Assert.Contains(observedTags, static tag => tag.Key == "nanto.outcome" && Equals(tag.Value, "ok"));
        Assert.DoesNotContain(observedTags, static tag => tag.Value?.ToString()?.Contains("app.nanto.invalid", StringComparison.Ordinal) == true);
    }

    private static NantoBridgeProtocolSession CreateSession(
        Func<JsonElement, NantoCommandContext, CancellationToken, ValueTask<NantoGeneratedCommandInvocation>> invoke)
    {
        var command = new TestCommand(invoke);
        var bridge = new NantoBridgeConfiguration();
        bridge.AddGenerated(new TestRegistration([command]));
        return new(
            bridge.CaptureSnapshot(),
            [new NantoFrontendCapability(command.Id, command.SymbolicName, NantoFrontendCapabilityKind.Command)],
            WindowId.Create(),
            new Uri("https://app.nanto.invalid"),
            new InlineDispatcher());
    }

    private static async Task<string> HandshakeAsync(NantoBridgeProtocolSession session)
    {
        string hello = $$"""{"v":1,"type":"hello","manifest":"{{session.ManifestFingerprint}}"}""";
        string? response = await session.HandleAsync(Encoding.UTF8.GetBytes(hello), TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(response!);
        return document.RootElement.GetProperty("session").GetString()!;
    }

    private static ReadOnlyMemory<byte> Invoke(string session, string? traceParent = null, string? traceState = null) => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { v = 1, type = "invoke", session, id = 1, command = 42, args = new { secret = "not-a-tag" }, traceparent = traceParent, tracestate = traceState }));

    private sealed class TestRegistration(IReadOnlyList<NantoGeneratedCommand> commands) : NantoGeneratedApiRegistration
    {
        public override IReadOnlyList<NantoGeneratedCommand> Commands { get; } = commands;

        public override IReadOnlyList<NantoGeneratedEvent> Events { get; } = [];
    }

    private sealed class TestCommand(
        Func<JsonElement, NantoCommandContext, CancellationToken, ValueTask<NantoGeneratedCommandInvocation>> invoke) : NantoGeneratedCommand
    {
        public override uint Id => 42;

        public override string SymbolicName => "projects.open";

        public override string ManifestEntry => "command:projects.open()->int|schemas:int=scalar";

        public override NantoGeneratedCommandKind Kind => NantoGeneratedCommandKind.Unary;

        public override ValueTask<NantoGeneratedCommandInvocation> InvokeAsync(
            JsonElement arguments,
            NantoCommandContext context,
            CancellationToken cancellationToken) => invoke(arguments, context, cancellationToken);
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