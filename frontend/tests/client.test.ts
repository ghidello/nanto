import assert from "node:assert/strict";
import test from "node:test";

import { NantoClient, NantoCommandError, NantoCommandErrorCode, type BridgeTransport } from "@nanto/core";
import { createApp, manifest } from "@nanto/app";

class FakeTransport implements BridgeTransport {
  readonly sent: object[] = [];
  #listener: ((message: string) => void) | undefined;

  public constructor(private readonly onPost?: (message: object) => void) {}

  post(message: string): void {
    const parsed = JSON.parse(message) as object;
    this.sent.push(parsed);
    this.onPost?.(parsed);
  }
  subscribe(listener: (message: string) => void): () => void { this.#listener = listener; return () => { this.#listener = undefined; }; }
  receive(message: object): void { this.#listener?.(JSON.stringify(message)); }
}

test("generated application client performs a typed unary call", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect(manifest);
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const app = createApp(client);
  const pending = app.projects.open(7);
  transport.receive({ type: "result", id: 1, value: { ok: true, value: { id: 7, window: "window" } } });
  assert.deepEqual(await pending, { ok: true, value: { id: 7, window: "window" } });
  assert.deepEqual(transport.sent[1], { v: 1, type: "invoke", session: "opaque", id: 1, command: 3836943207, args: { projectId: 7 } });
});

test("optional trace hooks propagate bounded W3C fields and observe completion", async () => {
  const transport = new FakeTransport();
  const observed: object[] = [];
  await using client = new NantoClient(transport, {
    traceContextProvider: {
      getTraceContext: () => ({
        traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        tracestate: "vendor=value",
      }),
    },
    traceObserver: {
      onCommandStart: event => observed.push({ type: "start", ...event }),
      onCommandEnd: event => observed.push({ type: "end", ...event }),
    },
  });
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;

  const pending = client.invoke<number>(7, {});
  transport.receive({ type: "result", id: 1, value: 42 });

  assert.equal(await pending, 42);
  assert.deepEqual(transport.sent[1], {
    v: 1,
    type: "invoke",
    session: "opaque",
    id: 1,
    command: 7,
    args: {},
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    tracestate: "vendor=value",
  });
  assert.equal((observed[0] as { type: string }).type, "start");
  assert.deepEqual(observed[1], { type: "end", id: 1, command: 7, outcome: "ok" });
});

test("failing or oversized trace hooks cannot change command behavior", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport, {
    traceContextProvider: { getTraceContext: () => ({ traceparent: "x".repeat(129) }) },
    traceObserver: {
      onCommandStart: () => { throw new Error("observer failed"); },
      onCommandEnd: () => { throw new Error("observer failed"); },
    },
  });
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;

  const pending = client.invoke<number>(7, {});
  transport.receive({ type: "result", id: 1, value: 42 });

  assert.equal(await pending, 42);
  assert.equal("traceparent" in transport.sent[1]!, false);
});

test("command failures expose only bounded symbolic error codes", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const pending = client.invoke(1, {});

  transport.receive({ type: "error", id: 1, code: "commandUnavailable" });

  await assert.rejects(pending, (error: unknown) => error instanceof NantoCommandError
    && error.code === NantoCommandErrorCode.CommandUnavailable);
});

test("unknown transport error codes are sanitized to internal", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const pending = client.invoke(1, {});

  transport.receive({ type: "error", id: 1, code: "nativeExceptionType" });

  await assert.rejects(pending, (error: unknown) => error instanceof NantoCommandError
    && error.code === NantoCommandErrorCode.Internal);
});

test("an already-aborted call rejects without sending invoke", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const controller = new AbortController();
  controller.abort();

  await assert.rejects(client.invoke(1, {}, controller.signal), { name: "AbortError" });
  assert.equal(transport.sent.length, 1);
});

test("aborting a pending invocation rejects immediately", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const controller = new AbortController();
  const pending = client.invoke(1, {}, controller.signal);

  controller.abort();

  await assert.rejects(pending, { name: "AbortError" });
  assert.deepEqual(transport.sent[2], { v: 1, type: "cancel", session: "opaque", id: 1 });
});

test("an abort during invoke posting still cancels native work in order", async () => {
  const controller = new AbortController();
  const transport = new FakeTransport(message => {
    if ((message as { type?: string }).type === "invoke") controller.abort();
  });
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;

  await assert.rejects(client.invoke(1, {}, controller.signal), { name: "AbortError" });

  assert.deepEqual(transport.sent.map(message => (message as { type: string }).type), ["hello", "invoke", "cancel"]);
});

test("aborting an event subscription settles a pending next call", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const controller = new AbortController();
  const subscription = client.subscribe(7, controller.signal);
  const pending = subscription.next();
  transport.receive({ type: "result", id: 1 });
  await new Promise(resolve => setImmediate(resolve));

  controller.abort();

  await assert.rejects(pending, { name: "AbortError" });
  assert.ok(transport.sent.some(message => JSON.stringify(message) === JSON.stringify({
    v: 1,
    type: "unsubscribe",
    session: "opaque",
    id: 1,
  })));
});

test("stream pulls are serialized and complete in order", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const stream = client.stream<number>(7, {});

  const first = stream.next();
  transport.receive({ type: "result", id: 1, stream: true });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal((transport.sent.at(-1) as { type: string }).type, "streamNext");
  transport.receive({ type: "item", id: 1, value: 10 });
  assert.deepEqual(await first, { done: false, value: 10 });

  const second = stream.next();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(transport.sent.filter(message => (message as { type: string }).type === "streamNext").length, 2);
  transport.receive({ type: "completion", id: 1 });
  assert.deepEqual(await second, { done: true, value: undefined });
});

test("returning an event subscription sends unsubscribe", async () => {
  const transport = new FakeTransport();
  await using client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const subscription = client.subscribe<number>(7);
  const first = subscription.next();
  transport.receive({ type: "result", id: 1 });
  transport.receive({ type: "item", id: 1, value: 10 });
  assert.deepEqual(await first, { done: false, value: 10 });

  await subscription.return(undefined);

  assert.ok(transport.sent.some(message => JSON.stringify(message) === JSON.stringify({
    v: 1,
    type: "unsubscribe",
    session: "opaque",
    id: 1,
  })));
});

test("a slow event consumer is terminated when its bounded buffer overflows", async () => {
  const transport = new FakeTransport();
  const client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const subscription = client.subscribe<number>(23);
  const opened = subscription.next();
  transport.receive({ type: "result", id: 1 });
  transport.receive({ type: "item", id: 1, value: -1 });
  assert.deepEqual(await opened, { done: false, value: -1 });

  for (let value = 0; value <= 64; value++) transport.receive({ type: "item", id: 1, value });

  assert.deepEqual(transport.sent[2], { v: 1, type: "unsubscribe", session: "opaque", id: 1 });
  for (let value = 0; value < 64; value++) assert.deepEqual(await subscription.next(), { done: false, value });
  await assert.rejects(subscription.next(), error => error instanceof NantoCommandError && error.code === NantoCommandErrorCode.ResourceExhausted);
  await client[Symbol.asyncDispose]();
});

test("event completion remains observable when the item buffer is full", async () => {
  const transport = new FakeTransport();
  const client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const subscription = client.subscribe<number>(23);
  const opened = subscription.next();
  transport.receive({ type: "result", id: 1 });
  transport.receive({ type: "item", id: 1, value: -1 });
  assert.deepEqual(await opened, { done: false, value: -1 });

  for (let value = 0; value < 64; value++) transport.receive({ type: "item", id: 1, value });
  transport.receive({ type: "completion", id: 1 });

  assert.equal(transport.sent.filter(message => (message as { type: string }).type === "unsubscribe").length, 0);
  for (let value = 0; value < 64; value++) assert.deepEqual(await subscription.next(), { done: false, value });
  assert.deepEqual(await subscription.next(), { done: true, value: undefined });
  await client[Symbol.asyncDispose]();
});

test("event overflow remains terminal when unsubscribe posting fails", async () => {
  const transport = new FakeTransport(message => {
    if ((message as { type?: string }).type === "unsubscribe") throw new Error("transport closed");
  });
  const client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const subscription = client.subscribe<number>(23);
  const opened = subscription.next();
  transport.receive({ type: "result", id: 1 });
  transport.receive({ type: "item", id: 1, value: -1 });
  assert.deepEqual(await opened, { done: false, value: -1 });

  for (let value = 0; value < 64; value++) transport.receive({ type: "item", id: 1, value });
  assert.doesNotThrow(() => transport.receive({ type: "item", id: 1, value: 64 }));

  for (let value = 0; value < 64; value++) assert.deepEqual(await subscription.next(), { done: false, value });
  await assert.rejects(subscription.next(), error => error instanceof NantoCommandError && error.code === NantoCommandErrorCode.ResourceExhausted);
  await client[Symbol.asyncDispose]();
});

test("aborting after event overflow does not resend unsubscribe", async () => {
  const transport = new FakeTransport();
  const client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const controller = new AbortController();
  const subscription = client.subscribe<number>(23, controller.signal);
  const opened = subscription.next();
  transport.receive({ type: "result", id: 1 });
  transport.receive({ type: "item", id: 1, value: -1 });
  assert.deepEqual(await opened, { done: false, value: -1 });

  for (let value = 0; value <= 64; value++) transport.receive({ type: "item", id: 1, value });
  controller.abort();

  assert.equal(transport.sent.filter(message => (message as { type: string }).type === "unsubscribe").length, 1);
  await subscription.return(undefined);
  await client[Symbol.asyncDispose]();
});

test("synchronous event overflow does not recreate unsubscribe cleanup", async () => {
  let transport: FakeTransport;
  transport = new FakeTransport(message => {
    if ((message as { type?: string }).type !== "subscribe") return;
    transport.receive({ type: "result", id: 1 });
    for (let value = 0; value <= 64; value++) transport.receive({ type: "item", id: 1, value });
  });
  const client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const subscription = client.subscribe<number>(23);

  assert.deepEqual(await subscription.next(), { done: false, value: 0 });
  await subscription.return(undefined);

  assert.equal(transport.sent.filter(message => (message as { type: string }).type === "unsubscribe").length, 1);
  await client[Symbol.asyncDispose]();
});

test("resuming a paused event subscription after client disposal rejects", async () => {
  const transport = new FakeTransport();
  const client = new NantoClient(transport);
  const connected = client.connect("manifest");
  transport.receive({ type: "ready", session: "opaque" });
  await connected;
  const subscription = client.subscribe<number>(23);
  const first = subscription.next();
  transport.receive({ type: "result", id: 1 });
  transport.receive({ type: "item", id: 1, value: 1 });
  assert.deepEqual(await first, { done: false, value: 1 });

  await client[Symbol.asyncDispose]();

  await assert.rejects(subscription.next(), error => error instanceof NantoCommandError && error.code === NantoCommandErrorCode.Internal);
});
