// Stage 3b orchestrator. Wires the bridge to the sidebar + workspace,
// reconciles every host `state` message into the DOM, routes pane.out /
// pane.exit to the right xterm.js, and binds keyboard shortcuts.

import "./style.css";

// Tag the document with the host so CSS can adapt — the mac host paints an
// opaque backdrop where Windows lets Mica show through (see tokens.css).
if (hostKind !== "none") document.documentElement.classList.add(`host-${hostKind}`);
// mac: the static chrome's shortcut labels say Ctrl; swap for the platform
// modifier once at boot (dynamic UI reads modKeyLabel directly).
if (modKeyLabel !== "Ctrl") {
  for (const el of document.querySelectorAll<HTMLElement>("[title]"))
    if (el.title.includes("Ctrl")) el.title = el.title.replaceAll("Ctrl", modKeyLabel);
  const hintTitle = document.querySelector(".shortcut-hint__title");
  if (hintTitle?.textContent) hintTitle.textContent = hintTitle.textContent.replace("Ctrl", modKeyLabel);
}

import { chordMod, hostKind, modKeyLabel, onMessage, send, type StateMessage, type SidebarMode } from "./bridge.js";
import { setHomeDir } from "./link-detect.js";
import { Sidebar } from "./sidebar.js";
import { Workspace } from "./workspace.js";
import { Dashboard } from "./dashboard.js";
import { installShortcutHint } from "./shortcut-hint.js";
import { Toast } from "./toast.js";
import { openSettings, applySettingsData, applyUpdateStatus } from "./settings.js";
import { showProjectsDialog } from "./projects-dialog.js";
import { showOnboarding } from "./onboarding.js";
import { startElapsedTicker } from "./elapsed.js";
import { startSpinnerTicker } from "./spinner.js";
import { confirmDialog } from "./confirm.js";
import { RestoreProgress } from "./restore-progress.js";
import { invalidateCommits } from "./commits.js";
import { initCloud } from "./cloud-panel.js";
import { initLocal } from "./local-panel.js";
import { initInspector, toggleInspector, openInspectorSearch } from "./inspector.js";
import { setModelLimits } from "./model-menu.js";
import { initWebPaneSuppression } from "./webpane-suppress.js";
import type { PaneTreeView } from "./bridge.js";

// One shared 1Hz ticker keeps every "working · 2m" label live without
// rebuilding the sidebar/dashboard. Safe to start before the first state.
startElapsedTicker();
// And one shared frame ticker for the working-tab braille spinners.
startSpinnerTicker();

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

// Cloud resources. Self-wiring: it owns its own chip + host listener, and stays
// completely invisible unless the host actually reports running machines.
initCloud();

// Local dev servers. Same self-wiring shape as cloud, one axis over: it owns its
// own sidebar card + host listener and stays invisible until something is
// actually listening on loopback.
initLocal();

// Inspector rail (right column). Same self-wiring shape: owns its DOM, listens
// for `state` itself to follow the focused pane, and fetches its own data via
// inspector.request. Open by default; the host ferries the persisted state in
// prefs.inspectorOpen on the first push.
initInspector();

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

// ── Sidebar mode (sessions | projects) ──────────────────────────────────────
// Renders the sidebar from `lastState` (declared below, set on every state
// push), so a page-local change — collapsing a project header — can redraw
// without waiting for a host round-trip.
function renderSidebar() {
  if (!lastState) return;
  sidebar.render(
    lastState.sessions,
    lastState.activeSessionId,
    lastState.closedSessions ?? [],
    lastState.projects ?? [],
    lastState.prefs?.sidebarMode ?? "sessions"
  );
  syncModeToggle(lastState.prefs?.sidebarMode ?? "sessions");
}
sidebar.rerender = renderSidebar;

const modeSessions = $<HTMLButtonElement>("mode-sessions");
const modeProjects = $<HTMLButtonElement>("mode-projects");

function syncModeToggle(mode: SidebarMode) {
  // aria-pressed is the source of truth the CSS selects on, so the toggle can't
  // show one thing while the list renders another.
  modeSessions.setAttribute("aria-pressed", String(mode === "sessions"));
  modeProjects.setAttribute("aria-pressed", String(mode === "projects"));
  // The plain "New session" button is meaningless in project mode — there, a
  // tab is created from its project's "+" so it lands in the right repo.
  $("new-session-button").hidden = mode === "projects";
}

modeSessions.addEventListener("click", () => send({ type: "ui.mode", mode: "sessions" }));
modeProjects.addEventListener("click", () => send({ type: "ui.mode", mode: "projects" }));

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

// ── Panel width mode (compact | standard) ───────────────────────────────────
// Toggles #app.app--wide, which swaps the two width tokens so both rails widen.
// Applied optimistically on click (so it feels instant) and persisted via
// prefs.wideLayout; the host ferries it back in every state push. Panes resize
// off their ResizeObserver when the grid columns change — no explicit refit,
// same contract as the sidebar collapse.
const layoutCompact = $<HTMLButtonElement>("layout-compact");
const layoutStandard = $<HTMLButtonElement>("layout-standard");
function applyLayout(wide: boolean) {
  appEl.classList.toggle("app--wide", wide);
  layoutCompact.setAttribute("aria-pressed", String(!wide));
  layoutStandard.setAttribute("aria-pressed", String(wide));
}
layoutCompact.addEventListener("click", () => {
  applyLayout(false);
  send({ type: "prefs.set", wideLayout: false });
});
layoutStandard.addEventListener("click", () => {
  applyLayout(true);
  send({ type: "prefs.set", wideLayout: true });
});

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
      // Home dir for "~\…" path expansion in the terminal HTML-file link menu.
      setHomeDir(msg.homeDir ?? "");
      // Drop any cached commit recap whose pane's ahead-count moved (a push or
      // a new commit) so the next hover/open refetches fresh.
      const walk = (n: PaneTreeView) => {
        if (n.kind === "leaf") invalidateCommits(n.paneId, n.ahead);
        else n.children.forEach(walk);
      };
      for (const sess of msg.sessions) walk(sess.rootPane);
      // Apply prefs BEFORE rendering panes so the very first Pane in a
      // freshly-launched app opens at the persisted font size instead of
      // briefly flashing the default 13px and then resizing on the next
      // tick. msg.prefs is always present (host always populates it).
      if (msg.prefs) workspace.applyPrefs(msg.prefs);
      // Reflect the persisted panel-width mode before panes render, so a
      // freshly-launched app opens at the right widths instead of flashing
      // Compact and then widening on the next tick.
      applyLayout(msg.prefs?.wideLayout ?? false);
      // Account-wide model limits for the per-pane model menu (usually empty).
      setModelLimits(msg.modelLimits);
      maybeShowOnboarding(msg.prefs);
      renderSidebar();
      // Pass the full session list + active id: the workspace keeps a stage
      // per session alive across switches (preserving terminal scrollback)
      // and disposes a stage only when its session drops out of this list.
      workspace.render(msg.sessions, msg.activeSessionId || null, msg.activePaneId || null);
      dashboard.render(msg.sessions);
      const active = activeOf(msg);
      setStatus(active ? `${active.title}  ${active.shell}` : "no session");
      break;
    }
    case "projects.candidates":
      showProjectsDialog(msg);
      break;
    case "pane.out":
      workspace.feed(msg.paneId, msg.b64);
      break;
    case "pane.exit":
      workspace.notifyExit(msg.paneId, msg.code);
      setStatus(`pane exited (${msg.code})`);
      break;
    case "pane.setup":
      // msg.colorIndex still arrives from the host; the cover no longer tints.
      workspace.setupOverlay(msg.paneId, msg.show);
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
    case "test.pointer": {
      // Stages for every session stay mounted and hide via
      // style.display="none"; target the visible one so the events land
      // where the user would click.
      const scope =
        [...document.querySelectorAll<HTMLElement>(".workspace__stage")]
          .find((s) => s.style.display !== "none") ?? document;
      const el = scope.querySelector(msg.selector);
      if (!el) break;
      const r = el.getBoundingClientRect();
      const cx = r.left + r.width / 2;
      const cy = r.top + r.height / 2;
      const base = { bubbles: true, cancelable: true, composed: true };
      if (msg.action === "contextmenu") {
        el.dispatchEvent(new MouseEvent("contextmenu", { ...base, clientX: cx, clientY: cy, button: 2 }));
      } else if (msg.action === "click") {
        el.dispatchEvent(new MouseEvent("click", { ...base, clientX: cx, clientY: cy, button: 0 }));
      } else if (msg.action === "drag") {
        const steps = 8;
        const dx = msg.dx ?? 0;
        const dy = msg.dy ?? 0;
        el.dispatchEvent(new PointerEvent("pointerdown",
          { ...base, clientX: cx, clientY: cy, button: 0, pointerId: 9999 }));
        for (let i = 1; i <= steps; i++) {
          el.dispatchEvent(new PointerEvent("pointermove",
            { ...base, clientX: cx + (dx * i) / steps, clientY: cy + (dy * i) / steps, pointerId: 9999 }));
        }
        el.dispatchEvent(new PointerEvent("pointerup",
          { ...base, clientX: cx + dx, clientY: cy + dy, pointerId: 9999 }));
      }
      break;
    }
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
    case "ui.urlpane.error":
      workspace.showUrlPaneError(msg.paneId, msg.message);
      break;
    case "board.state":
      workspace.applyBoardState(msg.paneId, msg.nodes, msg.links);
      break;
    case "board.error":
      workspace.showBoardError(msg.paneId, msg.message, msg.fatal === true);
      break;
    case "board.image.data":
      workspace.applyBoardImage(
        msg.paneId,
        msg.nodeId,
        msg.data ? `data:${msg.mediaType};base64,${msg.data}` : ""
      );
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
    case "pane.chooser":
      // A freshly-split terminal pane whose source pane had a known cwd —
      // show the in-pane chooser; its spawn is parked host-side until we
      // answer with pane.chooser.choose.
      workspace.showPaneChooser(msg);
      break;
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
    case "update.status":
      // Outcome of a manual Settings → "Check now"; reflected in the dialog.
      applyUpdateStatus(msg);
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
  if (!chordMod(ev) || ev.altKey) return;
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
  if (!chordMod(ev) || !ev.shiftKey) return;
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
    case "KeyE":
      // Even out panes: reset every split to equal sizing.
      workspace.distributeEven();
      ev.preventDefault(); ev.stopPropagation();
      break;
    case "KeyB":
      // Inspector rail. Deliberately mirrors Ctrl+B (left sidebar): B is "the
      // rail on the left", Ctrl+Shift+B is "the rail on the right". NOT
      // Ctrl+Shift+I — Chromium binds that to DevTools at the browser level, so
      // preventDefault here wouldn't stop it and both would fire.
      toggleInspector();
      ev.preventDefault(); ev.stopPropagation();
      break;
    case "KeyF":
      // Search the journal. Lives on the inspector because that's where the
      // transcript is; opens the rail first if it's collapsed.
      openInspectorSearch();
      ev.preventDefault(); ev.stopPropagation();
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

// Hide native web panes while any full-viewport modal is up (airspace fix).
initWebPaneSuppression();

// Paste into a board. The listener has to be on `document` — a non-focusable
// div never receives `paste` — so it sees every paste in the app and routes by
// ACTIVE PANE. A terminal's paste stays xterm's; taking it here would break
// Ctrl+V into a shell.
//
// Nothing suppresses Ctrl+V on the way here: AreBrowserAcceleratorKeysEnabled
// = false explicitly does not affect clipboard keys, and the capture-phase
// keydown handlers above all return before KeyV.
document.addEventListener("paste", (ev) => {
  if (workspace.handlePaste()) ev.preventDefault();
});

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
