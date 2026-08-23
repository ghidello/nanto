import React from "react";
import { createRoot } from "react-dom/client";
import { app } from "./nanto";
import "./style.css";

function App() {
  const [message, setMessage] = React.useState("The native frame is ready.");
  const greet = async () => setMessage(await app.app.greet("Nanto"));
  return <main><h1>NantoTemplateApp</h1><p>{message}</p><button onClick={greet}>Call native command</button></main>;
}

createRoot(document.getElementById("root")!).render(<App />);
window.chrome.webview.postMessage("nanto:ready:v1");
