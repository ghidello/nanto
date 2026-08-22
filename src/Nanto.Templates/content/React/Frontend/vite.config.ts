import { fileURLToPath, URL } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@nanto/app": fileURLToPath(new URL("./src/generated/nanto/app/index.ts", import.meta.url)),
      "@nanto/core": fileURLToPath(new URL("./src/generated/nanto/core/index.ts", import.meta.url)),
    },
  },
});
