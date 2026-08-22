import { createApp, manifest } from "@nanto/app";
import { NantoClient, type NantoTraceContext } from "@nanto/core";

declare global {
  interface Window {
    chrome: { webview: { postMessage(message: unknown): void; addEventListener(type: "message", listener: (event: MessageEvent) => void): void; removeEventListener(type: "message", listener: (event: MessageEvent) => void): void } };
  }
}

const transport = {
  post(message: string) { window.chrome.webview.postMessage(JSON.parse(message)); },
  subscribe(listener: (message: string) => void) {
    const receive = (event: MessageEvent) => listener(JSON.stringify(event.data));
    window.chrome.webview.addEventListener("message", receive);
    return () => window.chrome.webview.removeEventListener("message", receive);
  },
};

const randomHex = (bytes: number) => [...crypto.getRandomValues(new Uint8Array(bytes))].map(value => value.toString(16).padStart(2, "0")).join("");
const traceContext = (): NantoTraceContext => ({ traceparent: `00-${randomHex(16)}-${randomHex(8)}-01` });
const client = new NantoClient(transport, { traceContextProvider: { getTraceContext: traceContext } });
await client.connect(manifest);
const app = createApp(client);
document.querySelector<HTMLButtonElement>("#ping")!.addEventListener("click", async () => {
  document.querySelector<HTMLParagraphElement>("#result")!.textContent = await app.dependency.ping();
});
window.chrome.webview.postMessage("nanto:ready:v1");
