import React from "react";
import { createRoot } from "react-dom/client";
import { createApp, manifest } from "@nanto/app";
import { NantoClient } from "@nanto/core";
import "./style.css";

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
const app = createApp(client);

function App() {
  const [message, setMessage] = React.useState("The native frame is ready.");
  const greet = async () => setMessage(await app.app.greet("Nanto"));
  return <main><h1>NantoTemplateApp</h1><p>{message}</p><button onClick={greet}>Call native command</button></main>;
}

createRoot(document.getElementById("root")!).render(<App />);
window.chrome.webview.postMessage("nanto:ready:v1");
