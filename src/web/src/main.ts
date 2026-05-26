// Stage 3b orchestrator. Wires the bridge to the sidebar + workspace,
// reconciles every host `state` message into the DOM, routes pane.out /
// pane.exit to the right xterm.js, and binds keyboard shortcuts.

import "./style.css";

import { onMessage, send, type StateMessage } from "./bridge.js";
import { Sidebar } from "./sidebar.js";
import { Workspace } from "./workspace.js";
import { installShortcutHint } from "./shortcut-hint.js";
import { Toast } from "./toast.js";

const $ = <T extends HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} missing in index.html`);
  return el as T;
};

const sidebar = new Sidebar($("session-list"), $("new-session-button"));
const workspace = new Workspace($("workspace"));
const toast = new Toast($("toast"));
const statusEl = $("status-text");
installShortcutHint($("shortcut-hint"));

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
    case "toast":
      toast.show(msg.text, msg.level);
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

// Capture phase + ev.code: WPF's WebView2 hands keydown to xterm.js first
// because the terminal element has focus, and xterm's keydown listener
// turns Ctrl+letter into a control byte before the event ever bubbles
// up to window. Capture beats that.
//
// All four shortcuts require Ctrl+Shift to keep Ctrl+D (EOF), Ctrl+S
// (XOFF), Ctrl+W (delete-word) usable in the shell.
window.addEventListener("keydown", (ev) => {
  if (!ev.ctrlKey || !ev.shiftKey) return;
  const active = workspace.getActivePaneId();
  switch (ev.code) {
    case "KeyT":
      send({ type: "session.new" });
      ev.preventDefault(); ev.stopPropagation();
      break;
    case "KeyD":
      if (active) {
        send({ type: "pane.split", paneId: active, dir: "right" });
        ev.preventDefault(); ev.stopPropagation();
      }
      break;
    case "KeyS":
      if (active) {
        send({ type: "pane.split", paneId: active, dir: "down" });
        ev.preventDefault(); ev.stopPropagation();
      }
      break;
    case "KeyW":
      if (active) {
        send({ type: "pane.close", paneId: active });
        ev.preventDefault(); ev.stopPropagation();
      }
      break;
  }
}, /* useCapture */ true);

setStatus("connecting...");
send({ type: "ready" });

// lastState is kept for debugging; surface it for devtools poking.
(window as unknown as { __cmux: unknown }).__cmux = { get state() { return lastState; } };
