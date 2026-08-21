export const manifest = "b927e80e466c42381abaebd28f16a94fefab8a294974f95e5aa3c91916609fa7";
/**
 * @source Phase2TestApi.cs:87
 */
export const BuildError = {
    /**
     * @source Phase2TestApi.cs:89
     */
    Failed: "Failed",
};
/**
 * @source Phase2TestApi.cs:82
 */
export const OpenProjectError = {
    /**
     * @source Phase2TestApi.cs:84
     */
    NotFound: "NotFound",
};
export function createApp(client) {
    return {
        /**
         * @source Phase2TestApi.cs:6
         */
        projects: {
            /**
             * @source Phase2TestApi.cs:66
             */
            build: (projectId, $options) => client.stream(238439517, { projectId }, $options?.signal),
            /**
             * @source Phase2TestApi.cs:31
             */
            change: (projectId, $options) => client.invoke(3694782280, { projectId }, $options?.signal),
            /**
             * @source Phase2TestApi.cs:13
             */
            changed: {
                subscribe: (options) => client.subscribe(2877507624, options?.signal),
            },
            /**
             * @source Phase2TestApi.cs:38
             */
            getActiveWaitCount: ($options) => client.invoke(89415758, {}, $options?.signal),
            /**
             * @source Phase2TestApi.cs:35
             */
            getCancellationCount: ($options) => client.invoke(4212565545, {}, $options?.signal),
            /**
             * @source Phase2TestApi.cs:20
             */
            open: (projectId, $options) => client.invoke(3836943207, { projectId }, $options?.signal),
            /**
             * @source Phase2TestApi.cs:41
             */
            wait: ($options) => client.invoke(3813230800, {}, $options?.signal),
        },
    };
}
//# sourceMappingURL=index.js.map