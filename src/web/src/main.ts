// Stage 3b orchestrator. Wires the bridge to the sidebar + workspace,
// reconciles every host `state` message into the DOM, routes pane.out /
// pane.exit to the right xterm.js, and binds keyboard shortcuts.

import "./style.css";

import { onMessage, send, type StateMessage } from "./bridge.js";
import { Sidebar } from "./sidebar.js";
import { Workspace } from "./workspace.js";
import { Dashboard } from "./dashboard.js";
import { installShortcutHint } from "./shortcut-hint.js";
import { Toast } from "./toast.js";
import { openSettings, applySettingsData } from "./settings.js";
import { showOnboarding } from "./onboarding.js";
import { startElapsedTicker } from "./elapsed.js";
import { confirmDialog } from "./confirm.js";
import { RestoreProgress } from "./restore-progress.js";

// One shared 1Hz ticker keeps every "working · 2m" label live without
// rebuilding the sidebar/dashboard. Safe to start before the first state.
startElapsedTicker();

const $ = <T extends HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} missing in index.html`);
  return el as T;
};

const sidebar = new Sidebar($("sidebar-scroll"), $("new-session-button"), $("recently-closed"));
const workspace = new Workspace($("workspace"));
const dashboard = new Dashboard($("dashboard"), $("dash-badge"));
const toast = new Toast($("toast"));
const restoreProgress = new RestoreProgress();
const statusEl = $("status-text");

// Footer auto-update pill (hidden until the host reports a newer release).
const updateBanner = $<HTMLButtonElement>("update-banner");
const updateText = updateBanner.querySelector<HTMLElement>(".update-banner__text")!;
const updateAction = updateBanner.querySelector<HTMLElement>(".update-banner__action")!;
updateBanner.addEventListener("click", () => {
  // One-way trip: dim, swap to a progress label, and hand off to the host.
  // On success the process is replaced; update.error resets us on failure.
  updateBanner.classList.add("update-banner--busy");
  updateBanner.disabled = true;
  updateText.textContent = "Downloading update…";
  updateAction.textContent = "";
  send({ type: "update.apply" });
});

// One-time launch prompt is shown at most once per run; guard against a
// duplicate resume.prompt (defensive — the host sends it once).
let resumePromptShown = false;
installShortcutHint($("shortcut-hint"));

$("settings-button").addEventListener("click", () => openSettings());

// Dashboard: open via the ▦ sidebar button or Ctrl+Shift+A; Esc closes it.
$("open-dashboard").addEventListener("click", () => dashboard.toggle());
window.addEventListener("keydown", (ev) => {
  if (ev.key === "Escape" && dashboard.isOpen()) {
    dashboard.hide();
    ev.preventDefault();
    ev.stopPropagation();
  }
}, /* useCapture */ true);

// Sidebar collapse. The toggle button lives in the WPF title bar (see
// MainWindow.xaml's TitleBar.Header) and reaches the webview via the
// "ui.sidebar.toggle" message handled below. Ctrl+B is the keyboard
// shortcut. CSS handles the visual: #app's grid column shrinks and the
// sidebar effectively disappears.
const appEl = $("app");
function toggleSidebar() {
  appEl.classList.toggle("app--sidebar-collapsed");
  // The pane container resizes when the sidebar column changes width,
  // and the per-pane ResizeObserver fires xterm's fit addon automatically.
  // No explicit refit needed here.
}

let lastState: StateMessage | null = null;

// Auto-open the welcome lightbox once per launch on a fresh install. The host
// ferries `onboardingSeen` in every state push; we act on the first one only
// (dismissing it persists the flag, so later pushes carry seen=true anyway —
// the guard just avoids re-opening before that round-trip completes).
let onboardingChecked = false;
function maybeShowOnboarding(prefs?: { onboardingSeen?: boolean }) {
  if (onboardingChecked) return;
  onboardingChecked = true;
  if (!prefs?.onboardingSeen) showOnboarding();
}

function setStatus(text: string) { statusEl.textContent = text; }

function activeOf(s: StateMessage) {
  return s.sessions.find((sess) => sess.id === s.activeSessionId) ?? null;
}

onMessage((msg) => {
  switch (msg.type) {
    case "state": {
      lastState = msg;
      // Apply prefs BEFORE rendering panes so the very first Pane in a
      // freshly-launched app opens at the persisted font size instead of
      // briefly flashing the default 13px and then resizing on the next
      // tick. msg.prefs is always present (host always populates it).
      if (msg.prefs) workspace.applyPrefs(msg.prefs);
      maybeShowOnboarding(msg.prefs);
      sidebar.render(msg.sessions, msg.activeSessionId, msg.closedSessions ?? []);
      // Pass the full session list + active id: the workspace keeps a stage
      // per session alive across switches (preserving terminal scrollback)
      // and disposes a stage only when its session drops out of this list.
      workspace.render(msg.sessions, msg.activeSessionId || null, msg.activePaneId || null);
      dashboard.render(msg.sessions);
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
    case "toast":
      toast.show(
        msg.text,
        msg.level,
        msg.paneId ? workspace.paneElement(msg.paneId) : null,
      );
      break;
    case "settings.data":
      applySettingsData(msg);
      break;
    case "host.error":
      setStatus(`error: ${msg.message}`);
      break;
    case "ui.sidebar.toggle":
      toggleSidebar();
      break;
    case "ui.open-settings":
      openSettings();
      break;
    case "render.ping":
      // Reply immediately. This runs on the renderer's main-thread task
      // queue — the same queue that delivers keystrokes to xterm — so the
      // host's measured round-trip is a faithful proxy for input latency
      // under load. See scripts/test-perf-flow.ps1.
      send({ type: "render.pong", id: msg.id });
      break;
    case "ui.urlpane.relayout":
      // Host moved/resized — ask every UrlPane to re-emit its layout so
      // the corresponding child Window repositions. Workspace exposes
      // this through nudgeUrlPanes; cheap, just walks the pane map and
      // calls forceRefit on URL panes.
      workspace.nudgeUrlPanes();
      break;
    case "resume.prompt": {
      // One-time "reopen previous Claude sessions?" prompt. Until we answer,
      // the host holds the resumable panes' spawns, so a decision is required
      // to release them either way.
      if (resumePromptShown) break;
      resumePromptShown = true;
      const n = msg.paneCount;
      const sess = msg.sessionCount;
      const what =
        n === 1
          ? "1 Claude session from your last run can be reopened."
          : `${n} Claude sessions across ${sess} ${
              sess === 1 ? "project" : "projects"
            } can be reopened.`;
      confirmDialog({
        title: "Resume previous sessions?",
        body: `${what} They'll pick up where they left off.`,
        confirmLabel: "Resume",
        cancelLabel: "Not now",
      }).then((accept) => send({ type: "resume.decision", accept }));
      break;
    }
    case "restore.begin":
      restoreProgress.begin(msg.panes);
      break;
    case "restore.progress":
      restoreProgress.progress(msg.paneId, msg.state);
      break;
    case "restore.done":
      restoreProgress.finish();
      break;
    case "update.available":
      updateText.textContent = `Update to v${msg.version}`;
      updateAction.textContent = "Restart";
      updateBanner.disabled = false;
      updateBanner.classList.remove("update-banner--busy");
      updateBanner.hidden = false;
      break;
    case "update.error":
      updateBanner.classList.remove("update-banner--busy");
      updateBanner.disabled = false;
      updateText.textContent = "Update failed";
      updateAction.textContent = "Retry";
      toast.show(`Update failed: ${msg.message}`, "error", null);
      break;
  }
});

// ---- Keybindings -----------------------------------------------------------
// Match Windows Terminal: Ctrl+Shift+D = right, Ctrl+Shift+S = down,
// Ctrl+Shift+W = close pane, Ctrl+Shift+T = new session. Ctrl+Shift+C/V
// (clipboard) is handled by xterm.js; right-click copy/paste lives on
// each Pane's termHost (see pane.ts).

// Capture phase + ev.code: WPF's WebView2 hands keydown to xterm.js first
// because the terminal element has focus, and xterm's keydown listener
// turns Ctrl+letter into a control byte before the event ever bubbles
// up to window. Capture beats that.
//
// All four shortcuts require Ctrl+Shift to keep Ctrl+D (EOF), Ctrl+S
// (XOFF), Ctrl+W (delete-word) usable in the shell.
// Ctrl+B = toggle sidebar (Claude desktop convention, matches VS Code's
// "Toggle Side Bar" default). No Shift — we want it press-and-done.
// Ctrl+= / Ctrl++  → bump terminal font size in active pane.
// Ctrl+-           → shrink.
// Ctrl+0           → reset to default 13px.
window.addEventListener("keydown", (ev) => {
  if (!ev.ctrlKey || ev.altKey) return;
  if (ev.shiftKey && ev.code !== "Equal") return;   // allow Ctrl+Shift+= as Ctrl+=
  if (ev.code === "KeyB" && !ev.shiftKey) {
    toggleSidebar();
    ev.preventDefault();
    ev.stopPropagation();
    return;
  }
  const pane = workspace.getActivePane();
  if (!pane) return;
  if (ev.code === "Equal") {
    const size = pane.changeFontSize(+1);
    if (size) send({ type: "prefs.set", fontSize: size });
    ev.preventDefault();
    ev.stopPropagation();
  } else if (ev.code === "Minus") {
    const size = pane.changeFontSize(-1);
    if (size) send({ type: "prefs.set", fontSize: size });
    ev.preventDefault();
    ev.stopPropagation();
  } else if (ev.code === "Digit0") {
    const size = pane.resetFontSize();
    if (size) send({ type: "prefs.set", fontSize: size });
    ev.preventDefault();
    ev.stopPropagation();
  }
}, /* useCapture */ true);

window.addEventListener("keydown", (ev) => {
  if (!ev.ctrlKey || !ev.shiftKey) return;
  const active = workspace.getActivePaneId();
  switch (ev.code) {
    case "KeyA":
      dashboard.toggle();
      ev.preventDefault(); ev.stopPropagation();
      break;
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
    // Ctrl+Shift+arrows: move the active pane within its split. The host
    // reorders it among its siblings (no-op if the direction is across the
    // split's axis or the pane is already at the edge).
    case "ArrowLeft":
      if (active) { send({ type: "pane.moveDir", paneId: active, dir: "left" });  ev.preventDefault(); ev.stopPropagation(); }
      break;
    case "ArrowRight":
      if (active) { send({ type: "pane.moveDir", paneId: active, dir: "right" }); ev.preventDefault(); ev.stopPropagation(); }
      break;
    case "ArrowUp":
      if (active) { send({ type: "pane.moveDir", paneId: active, dir: "up" });    ev.preventDefault(); ev.stopPropagation(); }
      break;
    case "ArrowDown":
      if (active) { send({ type: "pane.moveDir", paneId: active, dir: "down" });  ev.preventDefault(); ev.stopPropagation(); }
      break;
  }
}, /* useCapture */ true);

setStatus("connecting...");
send({ type: "ready" });

// ---- Font / cell diagnostic --------------------------------------------
// Replaces the status sub-label with concrete, measurable values that
// prove which fonts are actually loaded and how big the terminal cells
// are rendering. Without this it's impossible to tell whether a CSS
// change landed at runtime; with it, the answer is on-screen.
//
// Detection method: render a known string in the target font and in a
// known wildly-different fallback ("monospace" generic), compare widths.
// If they differ, the target font is loaded. Works for both bundled
// (@font-face) and system-installed fonts.
function isFontLoaded(name: string): boolean {
  const probe = document.createElement("span");
  probe.style.position = "absolute";
  probe.style.visibility = "hidden";
  probe.style.whiteSpace = "nowrap";
  probe.style.fontSize = "72px";
  probe.textContent = "MMMiiii_0123456789";
  document.body.appendChild(probe);
  probe.style.fontFamily = "monospace";
  const fallback = probe.offsetWidth;
  probe.style.fontFamily = `"${name}", monospace`;
  const actual = probe.offsetWidth;
  document.body.removeChild(probe);
  return actual !== fallback && actual > 0;
}

function measureTerminalCell(): { w: number; h: number } | null {
  // Get cell height from xterm's own row element — that's the
  // authoritative value because it reflects fontSize × lineHeight after
  // pixel rounding. For width, render N copies of "M" in an independent
  // probe with the same font config and divide; xterm row spans contain
  // the full row text (~80 chars per row) so we can't read a single
  // char's width from them directly.
  const rowsEl = document.querySelector<HTMLElement>(".xterm-rows");
  if (!rowsEl || !rowsEl.firstElementChild) return null;
  const rowH = (rowsEl.firstElementChild as HTMLElement).offsetHeight;

  const probe = document.createElement("span");
  probe.style.position = "absolute";
  probe.style.visibility = "hidden";
  probe.style.whiteSpace = "pre";
  probe.style.fontSize = "13px";
  probe.style.lineHeight = "1";
  probe.style.fontFamily =
    '"Geist Mono Variable", "Cascadia Code", "Cascadia Mono", monospace';
  const N = 80;
  probe.textContent = "M".repeat(N);
  document.body.appendChild(probe);
  const cellW = probe.offsetWidth / N;
  document.body.removeChild(probe);
  return { w: Math.round(cellW * 10) / 10, h: rowH };
}

// Font diagnostic stays available on window.__fontDiag for DevTools
// poking; we no longer surface it in the sidebar footer now that we
// trust the bundle.
document.fonts.ready.then(() => {
  setTimeout(() => {
    (window as unknown as { __fontDiag: object }).__fontDiag = {
      inter: isFontLoaded("Inter Variable"),
      geistMono: isFontLoaded("Geist Mono Variable"),
      cascadiaCode: isFontLoaded("Cascadia Code"),
      cascadiaMono: isFontLoaded("Cascadia Mono"),
      cell: measureTerminalCell(),
    };
  }, 800);
});

// lastState is kept for debugging; surface it for devtools poking.
(window as unknown as { __perch: unknown }).__perch = { get state() { return lastState; } };
