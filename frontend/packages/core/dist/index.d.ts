export interface BridgeTransport {
    post(message: string): void;
    subscribe(listener: (message: string) => void): () => void;
}
export declare const NantoCommandErrorCode: {
    readonly CommandUnavailable: "commandUnavailable";
    readonly InvalidRequest: "invalidRequest";
    readonly ProtocolMismatch: "protocolMismatch";
    readonly ResourceExhausted: "resourceExhausted";
    readonly Internal: "internal";
};
export type NantoCommandErrorCode = typeof NantoCommandErrorCode[keyof typeof NantoCommandErrorCode];
export declare class NantoCommandError extends Error {
    readonly code: NantoCommandErrorCode;
    constructor(code: NantoCommandErrorCode);
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
