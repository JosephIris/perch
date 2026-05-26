// Stage 3b orchestrator. Wires the bridge to the sidebar + workspace,
// reconciles every host `state` message into the DOM, routes pane.out /
// pane.exit to the right xterm.js, and binds keyboard shortcuts.

import "./style.css";

import { onMessage, send, type StateMessage } from "./bridge.js";
import { Sidebar } from "./sidebar.js";
import { Workspace } from "./workspace.js";

const $ = <T extends HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} missing in index.html`);
  return el as T;
};

const sidebar = new Sidebar($("session-list"), $("new-session-button"));
const workspace = new Workspace($("workspace"));
const statusEl = $("status-text");

let lastState: StateMessage | null = null;

function setStatus(text: string) { statusEl.textContent = text; }

function activeOf(s: StateMessage) {
  return s.sessions.find((sess) => sess.id === s.activeSessionId) ?? null;
}

onMessage((msg) => {
  switch (msg.type) {
    case "state": {
      lastState = msg;
      sidebar.render(msg.sessions, msg.activeSessionId);
      const active = activeOf(msg);
      workspace.render(active, msg.activePaneId || null);
      setStatus(active ? `${active.title}  ${active.shell}` : "no session");
      break;
    }
    case "pane.out":
      workspace.feed(msg.paneId, msg.b64);
      break;
    case "pane.exit":
      workspace.notifyExit(msg.paneId, msg.code);
      setStatus(`pane exited (${msg.code})`);
      break;
    case "host.error":
      setStatus(`error: ${msg.message}`);
      break;
  }
});

// ---- Keybindings -----------------------------------------------------------
// Match Windows Terminal: Ctrl+Shift+D = right, Ctrl+Shift+S = down,
// Ctrl+Shift+W = close pane, Ctrl+Shift+T = new session. xterm.js handles
// Ctrl+C/V via OS clipboard already; we don't interfere.

window.addEventListener("keydown", (ev) => {
  if (!ev.ctrlKey || !ev.shiftKey) return;
  const active = workspace.getActivePaneId();
  switch (ev.key.toUpperCase()) {
    case "D":
      if (active) { send({ type: "pane.split", paneId: active, dir: "right" }); ev.preventDefault(); }
      break;
    case "S":
      if (active) { send({ type: "pane.split", paneId: active, dir: "down" }); ev.preventDefault(); }
      break;
    case "W":
      if (active) { send({ type: "pane.close", paneId: active }); ev.preventDefault(); }
      break;
    case "T":
      send({ type: "session.new" });
      ev.preventDefault();
      break;
  }
});

setStatus("connecting...");
send({ type: "ready" });

// lastState is kept for debugging; surface it for devtools poking.
(window as unknown as { __cmux: unknown }).__cmux = { get state() { return lastState; } };
