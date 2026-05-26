// Stage 3a entry point. Wires the bridge to the sidebar + workspace,
// reconciles every host `state` message into the DOM, and routes
// pane.out / pane.exit to the right xterm.js.

import "./style.css";

import { onMessage, send, type StateMessage } from "./bridge.js";
import { Sidebar } from "./sidebar.js";
import { Workspace } from "./workspace.js";

// ---- DOM hooks -------------------------------------------------------------

const $ = <T extends HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} missing in index.html`);
  return el as T;
};

const sidebar = new Sidebar($("session-list"), $("new-session-button"));
const workspace = new Workspace($("workspace"));
const statusEl = $("status-text");

let lastState: StateMessage | null = null;

function setStatus(text: string) {
  statusEl.textContent = text;
}

function activeOf(s: StateMessage) {
  return s.sessions.find((sess) => sess.id === s.activeSessionId) ?? null;
}

// ---- Host message handlers -------------------------------------------------

onMessage((msg) => {
  switch (msg.type) {
    case "state": {
      lastState = msg;
      sidebar.render(msg.sessions, msg.activeSessionId);
      workspace.render(activeOf(msg));
      const active = activeOf(msg);
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

// ---- Kickoff ---------------------------------------------------------------
// Tell the host we're ready; the host responds with a `state` snapshot and,
// for the active session's pane, starts pumping pane.out messages.
setStatus("connecting...");
send({ type: "ready" });
