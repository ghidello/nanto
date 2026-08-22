export const NantoCommandErrorCode = {
    CommandUnavailable: "commandUnavailable",
    InvalidRequest: "invalidRequest",
    ProtocolMismatch: "protocolMismatch",
    ResourceExhausted: "resourceExhausted",
    Internal: "internal",
};
export class NantoCommandError extends Error {
    code;
    constructor(code) {
        super(`Nanto command failed: ${code}`);
        this.code = code;
        this.name = "NantoCommandError";
    }
}
const eventBufferCapacity = 64;
export class NantoClient {
    #transport;
    #traceContextProvider;
    #traceObserver;
    #activeIds = new Set();
    #cleanupMessages = new Map();
    #queues = new Map();
    #buffered = new Map();
    #eventSubscriptions = new Set();
    #terminatedSubscriptions = new Set();
    #unsubscribe;
    #session;
    #nextId = 1;
    #disposed = false;
    constructor(transport, options = {}) {
        this.#transport = transport;
        this.#traceContextProvider = options.traceContextProvider;
        this.#traceObserver = options.traceObserver;
        this.#unsubscribe = transport.subscribe(message => this.#receive(JSON.parse(message)));
    }
    async connect(manifest) {
        if (this.#disposed)
            throw new NantoCommandError(NantoCommandErrorCode.Internal);
        this.#activeIds.add(0);
        const ready = new Promise((resolve, reject) => this.#queues.set(0, [{ resolve, reject }]));
        try {
            this.#transport.post(JSON.stringify({ v: 1, type: "hello", manifest }));
            const message = await ready;
            if (message.type !== "ready" || !message.session) {
                throw new NantoCommandError(this.#normalizeErrorCode(message.code, NantoCommandErrorCode.ProtocolMismatch));
            }
            this.#session = message.session;
        }
        finally {
            this.#clear(0);
        }
    }
    async invoke(command, args, signal) {
        signal?.throwIfAborted();
        const id = this.#allocateId();
        const context = this.#traceContext();
        let outcome = "error";
        let abort = () => undefined;
        this.#observeStart(id, command, context);
        try {
            this.#transport.post(JSON.stringify({ v: 1, type: "invoke", session: this.#requireSession(), id, command, args, ...context }));
            abort = this.#bindAbort(id, signal);
            const value = this.#value(await this.#next(id, signal));
            outcome = "ok";
            return value;
        }
        catch (error) {
            outcome = error instanceof DOMException && error.name === "AbortError" ? "cancelled" : "error";
            throw error;
        }
        finally {
            abort();
            this.#clear(id);
            this.#observeEnd(id, command, outcome);
        }
    }
    async *stream(command, args, signal) {
        signal?.throwIfAborted();
        const id = this.#allocateId();
        const context = this.#traceContext();
        let outcome = "error";
        let abort = () => undefined;
        this.#observeStart(id, command, context);
        try {
            this.#transport.post(JSON.stringify({ v: 1, type: "invoke", session: this.#requireSession(), id, command, args, ...context }));
            abort = this.#bindAbort(id, signal);
            const opened = await this.#next(id, signal);
            if (opened.type === "error")
                this.#throw(opened);
            while (true) {
                signal?.throwIfAborted();
                this.#transport.post(JSON.stringify({ v: 1, type: "streamNext", session: this.#requireSession(), id }));
                const message = await this.#next(id, signal);
                if (message.type === "completion") {
                    outcome = "ok";
                    return;
                }
                yield this.#value(message);
            }
        }
        catch (error) {
            outcome = error instanceof DOMException && error.name === "AbortError" ? "cancelled" : "error";
            throw error;
        }
        finally {
            abort();
            this.#transport.post(JSON.stringify({ v: 1, type: "cancel", session: this.#requireSession(), id }));
            this.#clear(id);
            this.#observeEnd(id, command, outcome);
        }
    }
    async *subscribe(event, signal) {
        signal?.throwIfAborted();
        const id = this.#allocateId();
        this.#eventSubscriptions.add(id);
        let abort = () => undefined;
        try {
            this.#transport.post(JSON.stringify({ v: 1, type: "subscribe", session: this.#requireSession(), id, event }));
            abort = this.#bindAbort(id, signal, "unsubscribe");
            const opened = await this.#next(id, signal);
            if (opened.type === "error")
                this.#throw(opened);
            while (true) {
                const message = await this.#next(id, signal);
                if (message.type === "completion")
                    return;
                yield this.#value(message);
            }
        }
        finally {
            abort();
            if (this.#cleanupMessages.has(id)) {
                this.#transport.post(JSON.stringify({ v: 1, type: "unsubscribe", session: this.#requireSession(), id }));
            }
            this.#clear(id);
        }
    }
    async [Symbol.asyncDispose]() {
        if (this.#disposed)
            return;
        this.#disposed = true;
        const cleanupFailures = [];
        if (this.#session) {
            for (const [id, type] of this.#cleanupMessages) {
                try {
                    this.#transport.post(JSON.stringify({ v: 1, type, session: this.#session, id }));
                }
                catch (error) {
                    cleanupFailures.push(error);
                }
            }
        }
        const disposed = new NantoCommandError(NantoCommandErrorCode.Internal);
        for (const queue of this.#queues.values()) {
            for (const waiter of queue)
                waiter.reject(disposed);
        }
        try {
            this.#unsubscribe();
        }
        catch (error) {
            cleanupFailures.push(error);
        }
        this.#activeIds.clear();
        this.#cleanupMessages.clear();
        this.#queues.clear();
        this.#buffered.clear();
        this.#eventSubscriptions.clear();
        this.#terminatedSubscriptions.clear();
        if (cleanupFailures.length)
            throw new AggregateError(cleanupFailures, "Nanto client cleanup failed.");
    }
    #receive(message) {
        if (message.type === "error" && message.id === undefined) {
            for (const id of this.#activeIds) {
                const queue = this.#queues.get(id);
                if (queue?.length) {
                    for (const waiter of queue.splice(0))
                        waiter.resolve(message);
                }
                else {
                    this.#buffered.set(id, [message]);
                }
            }
            return;
        }
        const id = message.id ?? 0;
        if (!this.#activeIds.has(id) || this.#terminatedSubscriptions.has(id))
            return;
        const queue = this.#queues.get(id);
        const waiter = queue?.shift();
        if (waiter)
            waiter.resolve(message);
        else {
            const buffered = this.#buffered.get(id) ?? [];
            if (message.type === "item" && this.#eventSubscriptions.has(id) && buffered.length >= eventBufferCapacity) {
                this.#terminatedSubscriptions.add(id);
                this.#buffered.set(id, [...buffered, { type: "error", id, code: NantoCommandErrorCode.ResourceExhausted }]);
                this.#cleanupMessages.delete(id);
                try {
                    this.#transport.post(JSON.stringify({ v: 1, type: "unsubscribe", session: this.#requireSession(), id }));
                }
                catch {
                    // The local terminal state must survive a transport teardown race.
                }
            }
            else {
                this.#buffered.set(id, [...buffered, message]);
            }
        }
    }
    #next(id, signal) {
        if (this.#disposed)
            throw new NantoCommandError(NantoCommandErrorCode.Internal);
        signal?.throwIfAborted();
        const buffered = this.#buffered.get(id)?.shift();
        if (buffered)
            return Promise.resolve(buffered);
        return new Promise((resolve, reject) => {
            const waiter = {
                resolve: message => {
                    signal?.removeEventListener("abort", abort);
                    resolve(message);
                },
                reject,
            };
            const abort = () => {
                const queue = this.#queues.get(id);
                if (queue)
                    this.#queues.set(id, queue.filter(candidate => candidate !== waiter));
                reject(signal?.reason ?? new DOMException("The operation was aborted.", "AbortError"));
            };
            signal?.addEventListener("abort", abort, { once: true });
            this.#queues.set(id, [...(this.#queues.get(id) ?? []), waiter]);
            if (signal?.aborted)
                abort();
        });
    }
    #clear(id) {
        this.#activeIds.delete(id);
        this.#cleanupMessages.delete(id);
        this.#queues.delete(id);
        this.#buffered.delete(id);
        this.#eventSubscriptions.delete(id);
        this.#terminatedSubscriptions.delete(id);
    }
    #value(message) {
        if (message.type === "error")
            this.#throw(message);
        return message.value;
    }
    #throw(message) {
        if (message.code === "cancelled")
            throw new DOMException("The operation was aborted.", "AbortError");
        throw new NantoCommandError(this.#normalizeErrorCode(message.code, NantoCommandErrorCode.Internal));
    }
    #bindAbort(id, signal, messageType = "cancel") {
        if (this.#terminatedSubscriptions.has(id))
            return () => undefined;
        this.#cleanupMessages.set(id, messageType);
        if (!signal)
            return () => undefined;
        const abort = () => {
            if (!this.#cleanupMessages.has(id))
                return;
            this.#transport.post(JSON.stringify({ v: 1, type: messageType, session: this.#requireSession(), id }));
        };
        signal.addEventListener("abort", abort, { once: true });
        if (signal.aborted)
            abort();
        return () => signal.removeEventListener("abort", abort);
    }
    #allocateId() {
        const id = this.#nextId++;
        if (this.#nextId > 0xffffffff)
            this.#nextId = 1;
        this.#activeIds.add(id);
        return id;
    }
    #requireSession() {
        if (this.#disposed)
            throw new NantoCommandError(NantoCommandErrorCode.Internal);
        if (!this.#session)
            throw new NantoCommandError(NantoCommandErrorCode.ProtocolMismatch);
        return this.#session;
    }
    #traceContext() {
        try {
            const context = this.#traceContextProvider?.getTraceContext();
            if (!context || typeof context.traceparent !== "string" || context.traceparent.length === 0 || context.traceparent.length > 128)
                return undefined;
            if (context.tracestate !== undefined && (typeof context.tracestate !== "string" || context.tracestate.length === 0 || context.tracestate.length > 512))
                return undefined;
            return context.tracestate === undefined ? { traceparent: context.traceparent } : { traceparent: context.traceparent, tracestate: context.tracestate };
        }
        catch {
            return undefined;
        }
    }
    #observeStart(id, command, context) {
        try {
            this.#traceObserver?.onCommandStart(context === undefined ? { id, command } : { id, command, context });
        }
        catch {
            // Diagnostics hooks must not change command behavior.
        }
    }
    #observeEnd(id, command, outcome) {
        try {
            this.#traceObserver?.onCommandEnd({ id, command, outcome });
        }
        catch {
            // Diagnostics hooks must not change command behavior.
        }
    }
    #normalizeErrorCode(code, fallback) {
        switch (code) {
            case NantoCommandErrorCode.CommandUnavailable:
            case NantoCommandErrorCode.InvalidRequest:
            case NantoCommandErrorCode.ProtocolMismatch:
            case NantoCommandErrorCode.ResourceExhausted:
            case NantoCommandErrorCode.Internal:
                return code;
            default:
                return fallback;
        }
    }
}
//# sourceMappingURL=index.js.map