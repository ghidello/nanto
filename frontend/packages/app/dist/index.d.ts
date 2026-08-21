import { NantoClient } from "@nanto/core";
export declare const manifest: "b927e80e466c42381abaebd28f16a94fefab8a294974f95e5aa3c91916609fa7";
export type NantoResult<T, TError> = {
    ok: true;
    value: T;
} | {
    ok: false;
    error: TError;
};
/**
 * @source Phase2TestApi.cs:80
 */
export interface BuildProgress {
    /**
     * @source Phase2TestApi.cs:80
     */
    percent: number;
    /**
     * @source Phase2TestApi.cs:80
     */
    projectId: number;
}
/**
 * @source Phase2TestApi.cs:87
 */
export declare const BuildError: {
    /**
     * @source Phase2TestApi.cs:89
     */
    readonly Failed: "Failed";
};
export type BuildError = typeof BuildError[keyof typeof BuildError];
/**
 * @source Phase2TestApi.cs:78
 */
export interface ProjectChange {
    /**
     * @source Phase2TestApi.cs:78
     */
    kind: string;
    /**
     * @source Phase2TestApi.cs:78
     */
    projectId: number;
}
/**
 * @source Phase2TestApi.cs:76
 */
export interface ProjectDetails {
    /**
     * @source Phase2TestApi.cs:76
     */
    id: number;
    /**
     * @source Phase2TestApi.cs:76
     */
    window: string;
}
/**
 * @source Phase2TestApi.cs:82
 */
export declare const OpenProjectError: {
    /**
     * @source Phase2TestApi.cs:84
     */
    readonly NotFound: "NotFound";
};
export type OpenProjectError = typeof OpenProjectError[keyof typeof OpenProjectError];
export declare function createApp(client: NantoClient): {
    /**
     * @source Phase2TestApi.cs:6
     */
    readonly projects: {
        /**
         * @source Phase2TestApi.cs:66
         */
        readonly build: (projectId: number, $options?: {
            signal?: AbortSignal;
        }) => AsyncGenerator<NantoResult<BuildProgress, "Failed">, any, any>;
        /**
         * @source Phase2TestApi.cs:31
         */
        readonly change: (projectId: number, $options?: {
            signal?: AbortSignal;
        }) => Promise<void>;
        /**
         * @source Phase2TestApi.cs:13
         */
        readonly changed: {
            readonly subscribe: (options?: {
                signal?: AbortSignal;
            }) => AsyncGenerator<ProjectChange, any, any>;
        };
        /**
         * @source Phase2TestApi.cs:38
         */
        readonly getActiveWaitCount: ($options?: {
            signal?: AbortSignal;
        }) => Promise<number>;
        /**
         * @source Phase2TestApi.cs:35
         */
        readonly getCancellationCount: ($options?: {
            signal?: AbortSignal;
        }) => Promise<number>;
        /**
         * @source Phase2TestApi.cs:20
         */
        readonly open: (projectId: number, $options?: {
            signal?: AbortSignal;
        }) => Promise<NantoResult<ProjectDetails, "NotFound">>;
        /**
         * @source Phase2TestApi.cs:41
         */
        readonly wait: ($options?: {
            signal?: AbortSignal;
        }) => Promise<void>;
    };
};
