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
  assert.deepEqual(transport.sent[1], { v: 1, type: "invoke", session: "opaque", id: 1, command: 1407742092, args: { projectId: 7 } });
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
