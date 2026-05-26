// Thin wrapper around WebView2's host bridge. All page <-> host messages
// flow through here so component code never reaches for chrome.webview
// directly and we can fake the host in plain-browser dev later if we want.

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(msg: unknown): void;
        addEventListener(
          type: "message",
          listener: (e: MessageEvent) => void
        ): void;
      };
    };
  }
}

// ---- Outgoing message shapes (page -> host) --------------------------------

export type OutMessage =
  | { type: "ready" }
  | { type: "pane.in"; paneId: string; b64: string }
  | { type: "pane.resize"; paneId: string; cols: number; rows: number }
  | { type: "pane.focus"; paneId: string }
  | { type: "pane.split"; paneId: string; dir: "right" | "down" }
  | { type: "pane.close"; paneId: string }
  | { type: "session.new"; shell?: string }
  | { type: "session.select"; id: string }
  | { type: "session.rename"; id: string; title: string }
  | { type: "session.close"; id: string };

// ---- Incoming message shapes (host -> page) --------------------------------

export type PaneTreeView =
  | { kind: "leaf"; paneId: string; name: string; url?: string | null }
  | { kind: "split"; orientation: "h" | "v"; children: PaneTreeView[] };

export type SessionView = {
  id: string;
  title: string;
  shell: string;
  rootPane: PaneTreeView;
};

export type StateMessage = {
  type: "state";
  activeSessionId: string;
  activePaneId: string;
  sessions: SessionView[];
};

export type InMessage =
  | StateMessage
  | { type: "pane.out"; paneId: string; b64: string }
  | { type: "pane.exit"; paneId: string; code: number }
  | { type: "host.error"; message: string };

// ---- Implementation --------------------------------------------------------

type Listener = (msg: InMessage) => void;

const listeners: Listener[] = [];

export function send(msg: OutMessage): void {
  const wire = JSON.stringify(msg);
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(wire);
  } else {
    console.log("[bridge.no-host]", wire);
  }
}

export function onMessage(listener: Listener): void {
  listeners.push(listener);
}

function dispatch(raw: unknown) {
  let msg: InMessage;
  if (typeof raw === "string") {
    try { msg = JSON.parse(raw) as InMessage; }
    catch { console.warn("[bridge] non-JSON string from host:", raw); return; }
  } else if (raw && typeof raw === "object") {
    msg = raw as InMessage;
  } else {
    return;
  }
  for (const l of listeners) {
    try { l(msg); }
    catch (err) { console.error("[bridge] listener threw:", err); }
  }
}

if (window.chrome?.webview) {
  window.chrome.webview.addEventListener("message", (e: MessageEvent) => {
    dispatch(e.data);
  });
} else {
  console.warn("[bridge] no chrome.webview -- running in plain browser");
}

// ---- Base64 helpers --------------------------------------------------------
// Round-trip bytes byte-for-byte. We avoid TextEncoder/TextDecoder for
// PTY output because the shell can emit partial multi-byte sequences
// (an emoji split across reads); xterm.js's VT parser handles reassembly.

export function b64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function bytesToB64(buf: Uint8Array): string {
  const CHUNK = 0x8000;
  let s = "";
  for (let i = 0; i < buf.length; i += CHUNK) {
    s += String.fromCharCode(...buf.subarray(i, i + CHUNK));
  }
  return btoa(s);
}
