export const manifest = "1e1306342fd5b6a5f1b1439ddf72269e6af8a2ff6b70e475084b5b7faf1ec44a";
export const BuildError = {
    Failed: "Failed",
};
export const OpenProjectError = {
    NotFound: "NotFound",
};
export function createApp(client) {
    return {
        projects: {
            build: (projectId, $options) => client.stream(1725757109, { projectId }, $options?.signal),
            changed: {
                subscribe: (options) => client.subscribe(2877507624, options?.signal),
            },
            open: (projectId, $options) => client.invoke(1407742092, { projectId }, $options?.signal),
        },
    };
}
//# sourceMappingURL=index.js.map