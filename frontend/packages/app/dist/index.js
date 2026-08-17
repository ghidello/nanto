export const manifest = "7b9802242b0911cc20c6060eb6650193ea342f9b27ab2803526a5615b6e37363";
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
            build: (projectId, $options) => client.stream(1725757109, { projectId }, $options?.signal),
            /**
             * @source Phase2TestApi.cs:31
             */
            change: (projectId, $options) => client.invoke(3648429410, { projectId }, $options?.signal),
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
            open: (projectId, $options) => client.invoke(1407742092, { projectId }, $options?.signal),
            /**
             * @source Phase2TestApi.cs:41
             */
            wait: ($options) => client.invoke(1278557076, {}, $options?.signal),
        },
    };
}
//# sourceMappingURL=index.js.map