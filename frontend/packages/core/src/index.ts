export interface BridgeTransport {
  post(message: string): void;
  subscribe(listener: (message: string) => void): () => void;
}

export class NantoCommandError extends Error {
  public constructor(public readonly code: string) {
    super(`Nanto command failed: ${code}`);
    this.name = "NantoCommandError";
  }
}

type Message = { type: string; id?: number; session?: string; code?: string; value?: unknown; stream?: boolean };
type Waiter = { resolve(message: Message): void; reject(reason: unknown): void };

export class NantoClient implements AsyncDisposable {
  readonly #transport: BridgeTransport;
  readonly #activeIds = new Set<number>();
  readonly #cleanupMessages = new Map<number, "cancel" | "unsubscribe">();
  readonly #queues = new Map<number, Waiter[]>();
  readonly #buffered = new Map<number, Message[]>();
  readonly #unsubscribe: () => void;
  #session: string | undefined;
  #nextId = 1;
  #disposed = false;

  public constructor(transport: BridgeTransport) {
    this.#transport = transport;
    this.#unsubscribe = transport.subscribe(message => this.#receive(JSON.parse(message) as Message));
  }

  public async connect(manifest: string): Promise<void> {
    if (this.#disposed) throw new NantoCommandError("internal");
    this.#activeIds.add(0);
    const ready = new Promise<Message>((resolve, reject) => this.#queues.set(0, [{ resolve, reject }]));
    try {
      this.#transport.post(JSON.stringify({ v: 1, type: "hello", manifest }));
      const message = await ready;
      if (message.type !== "ready" || !message.session) throw new NantoCommandError(message.code ?? "protocolMismatch");
      this.#session = message.session;
    } finally {
      this.#clear(0);
    }
  }

  public async invoke<T>(command: number, args: object, signal?: AbortSignal): Promise<T> {
    signal?.throwIfAborted();
    const id = this.#allocateId();
    let abort: () => void = () => undefined;
    try {
      this.#transport.post(JSON.stringify({ v: 1, type: "invoke", session: this.#requireSession(), id, command, args }));
      abort = this.#bindAbort(id, signal);
      return this.#value<T>(await this.#next(id, signal));
    } finally {
      abort();
      this.#clear(id);
    }
  }

  public async *stream<T>(command: number, args: object, signal?: AbortSignal): AsyncGenerator<T> {
    signal?.throwIfAborted();
    const id = this.#allocateId();
    let abort: () => void = () => undefined;
    try {
      this.#transport.post(JSON.stringify({ v: 1, type: "invoke", session: this.#requireSession(), id, command, args }));
      abort = this.#bindAbort(id, signal);
      const opened = await this.#next(id, signal);
      if (opened.type === "error") this.#throw(opened);
      while (true) {
        signal?.throwIfAborted();
        this.#transport.post(JSON.stringify({ v: 1, type: "streamNext", session: this.#requireSession(), id }));
        const message = await this.#next(id, signal);
        if (message.type === "completion") return;
        yield this.#value<T>(message);
      }
    } finally {
      abort();
      this.#transport.post(JSON.stringify({ v: 1, type: "cancel", session: this.#requireSession(), id }));
      this.#clear(id);
    }
  }

  public async *subscribe<T>(event: number, signal?: AbortSignal): AsyncGenerator<T> {
    signal?.throwIfAborted();
    const id = this.#allocateId();
    let abort: () => void = () => undefined;
    try {
      this.#transport.post(JSON.stringify({ v: 1, type: "subscribe", session: this.#requireSession(), id, event }));
      abort = this.#bindAbort(id, signal, "unsubscribe");
      const opened = await this.#next(id, signal);
      if (opened.type === "error") this.#throw(opened);
      while (true) {
        const message = await this.#next(id, signal);
        if (message.type === "completion") return;
        yield this.#value<T>(message);
      }
    } finally {
      abort();
      this.#transport.post(JSON.stringify({ v: 1, type: "unsubscribe", session: this.#requireSession(), id }));
      this.#clear(id);
    }
  }

  public async [Symbol.asyncDispose](): Promise<void> {
    if (this.#disposed) return;
    this.#disposed = true;
    const cleanupFailures: unknown[] = [];
    if (this.#session) {
      for (const [id, type] of this.#cleanupMessages) {
        try {
          this.#transport.post(JSON.stringify({ v: 1, type, session: this.#session, id }));
        } catch (error) {
          cleanupFailures.push(error);
        }
      }
    }
    const disposed = new NantoCommandError("internal");
    for (const queue of this.#queues.values()) {
      for (const waiter of queue) waiter.reject(disposed);
    }
    try {
      this.#unsubscribe();
    } catch (error) {
      cleanupFailures.push(error);
    }
    this.#activeIds.clear();
    this.#cleanupMessages.clear();
    this.#queues.clear();
    this.#buffered.clear();
    if (cleanupFailures.length) throw new AggregateError(cleanupFailures, "Nanto client cleanup failed.");
  }

  #receive(message: Message): void {
    if (message.type === "error" && message.id === undefined) {
      for (const id of this.#activeIds) {
        const queue = this.#queues.get(id);
        if (queue?.length) {
          for (const waiter of queue.splice(0)) waiter.resolve(message);
        } else {
          this.#buffered.set(id, [message]);
        }
      }
      return;
    }

    const id = message.id ?? 0;
    if (!this.#activeIds.has(id)) return;
    const queue = this.#queues.get(id);
    const waiter = queue?.shift();
    if (waiter) waiter.resolve(message);
    else this.#buffered.set(id, [...(this.#buffered.get(id) ?? []), message]);
  }

  #next(id: number, signal?: AbortSignal): Promise<Message> {
    signal?.throwIfAborted();
    const buffered = this.#buffered.get(id)?.shift();
    if (buffered) return Promise.resolve(buffered);
    return new Promise((resolve, reject) => {
      const waiter: Waiter = {
        resolve: message => {
          signal?.removeEventListener("abort", abort);
          resolve(message);
        },
        reject,
      };
      const abort = () => {
        const queue = this.#queues.get(id);
        if (queue) this.#queues.set(id, queue.filter(candidate => candidate !== waiter));
        reject(signal?.reason ?? new DOMException("The operation was aborted.", "AbortError"));
      };
      signal?.addEventListener("abort", abort, { once: true });
      this.#queues.set(id, [...(this.#queues.get(id) ?? []), waiter]);
      if (signal?.aborted) abort();
    });
  }

  #clear(id: number): void {
    this.#activeIds.delete(id);
    this.#cleanupMessages.delete(id);
    this.#queues.delete(id);
    this.#buffered.delete(id);
  }

  #value<T>(message: Message): T {
    if (message.type === "error") this.#throw(message);
    return message.value as T;
  }

  #throw(message: Message): never {
    if (message.code === "cancelled") throw new DOMException("The operation was aborted.", "AbortError");
    throw new NantoCommandError(message.code ?? "internal");
  }

  #bindAbort(id: number, signal?: AbortSignal, messageType: "cancel" | "unsubscribe" = "cancel"): () => void {
    this.#cleanupMessages.set(id, messageType);
    if (!signal) return () => undefined;
    const abort = () => this.#transport.post(JSON.stringify({ v: 1, type: messageType, session: this.#requireSession(), id }));
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
    return () => signal.removeEventListener("abort", abort);
  }

  #allocateId(): number {
    const id = this.#nextId++;
    if (this.#nextId > 0xffffffff) this.#nextId = 1;
    this.#activeIds.add(id);
    return id;
  }

  #requireSession(): string {
    if (this.#disposed) throw new NantoCommandError("internal");
    if (!this.#session) throw new NantoCommandError("protocolMismatch");
    return this.#session;
  }
}
