import { createApp, manifest } from "@nanto/app";
import { NantoClient } from "@nanto/core";

declare global {
  interface Window {
    chrome: {
      webview: {
        postMessage(message: unknown): void;
        addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
        removeEventListener(type: "message", listener: (event: MessageEvent) => void): void;
      };
    };
  }
}

const transport = {
  post(message: string) {
    window.chrome.webview.postMessage(JSON.parse(message));
  },
  subscribe(listener: (message: string) => void) {
    const receive = (event: MessageEvent) => listener(JSON.stringify(event.data));
    window.chrome.webview.addEventListener("message", receive);
    return () => window.chrome.webview.removeEventListener("message", receive);
  },
};

const client = new NantoClient(transport);
await client.connect(manifest);

export const app = createApp(client);
