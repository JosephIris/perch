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
  /* When `url` is set the new leaf is a webview pane (iframe) instead of
   * a terminal. Used by the URL action menu's "Open in pane right/down". */
  | { type: "pane.split"; paneId: string; dir: "right" | "down"; url?: string }
  | { type: "pane.close"; paneId: string }
  | { type: "session.new"; shell?: string }
  | { type: "session.select"; id: string }
  | { type: "session.rename"; id: string; title: string }
  | { type: "session.close"; id: string }
  /* Open a URL externally — host resolves to the OS default browser. */
  | { type: "url.open"; url: string }
  /* Pane rename + color tag changes from the pane header chrome. */
  | { type: "pane.rename"; paneId: string; name: string }
  | { type: "pane.recolor"; paneId: string; colorIndex: number }
  /* Pane cwd update from xterm's OSC 7 handler. Host uses it to auto-fill
   * the branch chip via `git rev-parse`. */
  | { type: "pane.cwd"; paneId: string; cwd: string }
  /* URL pane layout — page reports a rect for the placeholder; host
   * sizes a real WebView2 control to match. First layout creates the
   * WebView2; subsequent layouts reposition/resize. */
  | { type: "urlpane.layout"; paneId: string; url: string; x: number; y: number; w: number; h: number }
  | { type: "urlpane.dispose"; paneId: string };

// ---- Incoming message shapes (host -> page) --------------------------------

export type AgentStateName = "idle" | "working" | "waiting" | "done";
export type NotificationLevel = "info" | "success" | "warn" | "error";

export type PaneTreeView =
  | {
      kind: "leaf";
      paneId: string;
      name: string;
      url?: string | null;
      /* Color tag (0–5) into the pane palette in style.css. */
      colorIndex: number;
      /* Per-pane agent state — pane header surfaces this directly so
       * each pane's status is visible without going through the sidebar. */
      agentState: AgentStateName;
      activityDetail: string;
      branch: string;
      ports: number[];
      notification: { text: string; level: NotificationLevel } | null;
      /* Commits made since the agent session's baseline. 0 when no
       * baseline is set. */
      commitCount: number;
    }
  | { kind: "split"; orientation: "h" | "v"; children: PaneTreeView[] };

export type SessionView = {
  id: string;
  title: string;
  shell: string;
  rootPane: PaneTreeView;
  /* Session-level fields are aggregations of the panes' per-pane state.
   * agentState = most-urgent across panes (waiting > working > done > idle). */
  agentState: AgentStateName;
  activityDetail: string;
  branch: string;
  ports: number[];
  notification: { text: string; level: NotificationLevel } | null;
  /* Pane-count breakdown so the sidebar can render "3 panes · 1 waiting". */
  paneCount: number;
  waitingCount: number;
  workingCount: number;
};

export type StateMessage = {
  type: "state";
  activeSessionId: string;
  activePaneId: string;
  sessions: SessionView[];
};

export type ToastMessage = {
  type: "toast";
  text: string;
  level: NotificationLevel;
};

export type InMessage =
  | StateMessage
  | { type: "pane.out"; paneId: string; b64: string }
  | { type: "pane.exit"; paneId: string; code: number }
  | ToastMessage
  | { type: "host.error"; message: string }
  /* UI commands the WPF host can issue to the webview (e.g. a chrome
   * button in the title bar telling the webview to flip a class). */
  | { type: "ui.sidebar.toggle" }
  /* Triggered on main-window move/resize so URL panes re-emit their
   * placeholder rect and the host can reposition the child Windows. */
  | { type: "ui.urlpane.relayout" };

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
