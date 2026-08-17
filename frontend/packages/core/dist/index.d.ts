export interface BridgeTransport {
    post(message: string): void;
    subscribe(listener: (message: string) => void): () => void;
}
export declare class NantoCommandError extends Error {
    readonly code: string;
    constructor(code: string);
}
export declare class NantoClient implements AsyncDisposable {
    #private;
    constructor(transport: BridgeTransport);
    connect(manifest: string): Promise<void>;
    invoke<T>(command: number, args: object, signal?: AbortSignal): Promise<T>;
    stream<T>(command: number, args: object, signal?: AbortSignal): AsyncGenerator<T>;
    subscribe<T>(event: number, signal?: AbortSignal): AsyncGenerator<T>;
    [Symbol.asyncDispose](): Promise<void>;
}
