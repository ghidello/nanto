import { NantoClient } from "@nanto/core";
export declare const manifest: "1e1306342fd5b6a5f1b1439ddf72269e6af8a2ff6b70e475084b5b7faf1ec44a";
export type NantoResult<T, TError> = {
    ok: true;
    value: T;
} | {
    ok: false;
    error: TError;
};
export interface BuildProgress {
    percent: number;
    projectId: number;
}
export declare const BuildError: {
    readonly Failed: "Failed";
};
export type BuildError = typeof BuildError[keyof typeof BuildError];
export interface ProjectChange {
    kind: string;
    projectId: number;
}
export interface ProjectDetails {
    id: number;
    window: string;
}
export declare const OpenProjectError: {
    readonly NotFound: "NotFound";
};
export type OpenProjectError = typeof OpenProjectError[keyof typeof OpenProjectError];
export declare function createApp(client: NantoClient): {
    readonly projects: {
        readonly build: (projectId: number, $options?: {
            signal?: AbortSignal;
        }) => AsyncGenerator<NantoResult<BuildProgress, "Failed">, any, any>;
        readonly changed: {
            readonly subscribe: (options?: {
                signal?: AbortSignal;
            }) => AsyncGenerator<ProjectChange, any, any>;
        };
        readonly open: (projectId: number, $options?: {
            signal?: AbortSignal;
        }) => Promise<NantoResult<ProjectDetails, "NotFound">>;
    };
};
