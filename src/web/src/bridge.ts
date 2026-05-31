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
  /* Backpressure ack: sent once xterm finishes writing a pane.out chunk so
   * the host can shrink that PTY's unacked backlog and resume reading.
   * `bytes` is the ORIGINAL pane.out byte count (pre-underline-injection),
   * matching what the host counted when it sent the chunk. */
  | { type: "pane.ack"; paneId: string; bytes: number }
  | { type: "pane.resize"; paneId: string; cols: number; rows: number }
  /* Test-only: reply to a host render.ping, measuring renderer round-trip. */
  | { type: "render.pong"; id: number }
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
  | { type: "urlpane.dispose"; paneId: string }
  /* User preferences (terminal font size, etc.) — host persists to
   * Settings.cs so it survives restart. */
  | { type: "prefs.set"; fontSize?: number }
  /* Settings dialog: page asks the host for current settings + the list
   * of detected shells, host replies with a settings.data message. */
  | { type: "settings.request" }
  /* Settings dialog save. Each field is optional so the page can send a
   * partial update; the host only overwrites provided keys. defaultShell
   * is the shell COMMAND LINE (matching one of settings.data.shells[].cmd)
   * or "" for auto-detect. */
  | { type: "settings.save"; defaultShell?: string; defaultCwd?: string; fontSize?: number };

// ---- Incoming message shapes (host -> page) --------------------------------

export type AgentStateName = "idle" | "working" | "waiting" | "permission";
export type NotificationLevel = "info" | "success" | "warn" | "error";

export type PaneTreeView =
  | {
      kind: "leaf";
      paneId: string;
      name: string;
      /* Full first-prompt text the label was cut from; shown in the pane
       * header hover tooltip. Empty when the pane wasn't auto-named. */
      nameFull?: string;
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
  /* User preferences ferried with every state push — cheap and means the
   * page never has to ask. fontSize is the default terminal cell size
   * applied to new Panes; existing panes follow it too on every state. */
  prefs: { fontSize: number };
};

export type ToastMessage = {
  type: "toast";
  text: string;
  level: NotificationLevel;
  /* Pane that fired the notify. The page anchors the toast to that pane's
   * bottom-center; absent / not-in-view falls back to window-centered. */
  paneId?: string;
};

/* Reply to settings.request. shells is the host's detected-shell list;
 * cmd is the command line to store as defaultShell. defaultCwdResolved is
 * what an empty defaultCwd falls back to (shown as the input placeholder
 * so the user sees where new sessions actually land). */
export type SettingsDataMessage = {
  type: "settings.data";
  shells: { name: string; cmd: string }[];
  defaultShell: string;
  defaultCwd: string;
  defaultCwdResolved: string;
  fontSize: number;
};

export type InMessage =
  | StateMessage
  | { type: "pane.out"; paneId: string; b64: string }
  | { type: "pane.exit"; paneId: string; code: number }
  | ToastMessage
  | SettingsDataMessage
  | { type: "host.error"; message: string }
  /* UI commands the WPF host can issue to the webview (e.g. a chrome
   * button in the title bar telling the webview to flip a class). */
  | { type: "ui.sidebar.toggle" }
  /* Triggered on main-window move/resize so URL panes re-emit their
   * placeholder rect and the host can reposition the child Windows. */
  | { type: "ui.urlpane.relayout" }
  /* Test-only: host asks the page to round-trip a marker through its main
   * thread so the host can time renderer responsiveness under load. */
  | { type: "render.ping"; id: number }
  /* Host asks the page to open the settings dialog (title-bar gear or the
   * test harness). The page already has the open path wired to the
   * sidebar gear; this just lets the host trigger it too. */
  | { type: "ui.open-settings" };

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
