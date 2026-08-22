import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";

export default defineConfig({
  resolve: {
    alias: {
      "@nanto/app": fileURLToPath(new URL("./src/generated/nanto/app/index.ts", import.meta.url)),
      "@nanto/core": fileURLToPath(new URL("./src/generated/nanto/core/index.ts", import.meta.url)),
    },
  },
});
