// Visual-validation harness (NOT shipped — not imported by main.ts, so it
// never lands in app.js). Renders the REAL Sidebar / Dashboard / confirm code
// with hand-built sample SessionView data covering every agent state, so the
// state-conditional rows, diff chips, top-right X, and confirm modal can be
// screenshotted without launching the full app / touching a live session.
//
// Build with `cd src/web && npm run build:harness` (-> design-loop/harness.{js,
// css}), then open design-loop/harness.html in a browser. The view is chosen by
// location.hash (#sidebar | #dashboard | #confirm).

import "./style.css";
import { startElapsedTicker } from "./elapsed.js";
import { startSpinnerTicker } from "./spinner.js";
import { Sidebar } from "./sidebar.js";
import { Dashboard } from "./dashboard.js";
import { confirmDialog } from "./confirm.js";
import { showPaneChooser } from "./pane-chooser.js";
import { buildPaneFooter, applyPaneFooter } from "./pane-footer.js";
import { buildPaneHeader, applyChips, applyPorts, applyModelChip, applyAgentBadge } from "./pane-header.js";
import { showBrowserPrompt } from "./browser-prompt.js";
import { showModelMenu, dismissModelMenu, setModelLimits } from "./model-menu.js";
import { showNewTabDialog } from "./new-tab-dialog.js";
import { RestoreProgress } from "./restore-progress.js";
import { openCommitsPopover, openCommitsLightbox } from "./commits-view.js";
import { showCloudPanel, applyCloudData } from "./cloud-panel.js";
import type { SessionView, PaneTreeView } from "./bridge.js";

type Leaf = Extract<PaneTreeView, { kind: "leaf" }>;

function leaf(over: Partial<Leaf>): Leaf {
  return {
    kind: "leaf",
    paneId: Math.random().toString(36).slice(2),
    name: "pane",
    colorIndex: 0,
    agentState: "idle",
    activityDetail: "",
    branch: "main",
    ports: [],
    notification: null,
    commitCount: 0,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    ...over,
  };
}

// Sample "working since 2m ago" for the live-elapsed label, and a turn that
// finished ~4m ago for the live "finished · Xm ago" on done rows.
const TWO_MIN_AGO = Date.now() - 125_000;
const FOUR_MIN_AGO = Date.now() - 248_000;

const sessions: SessionView[] = [
  // Working, single pane — sidebar shows "▸ {action}".
  {
    id: "s-working",
    title: "nadec-api live animations",
    shell: "pwsh",
    projectId: "",
    worktreeBranch: "",
    rootPane: leaf({ name: "nadec-api", agentState: "working", activityDetail: "editing live-updates.ts", branch: "main", ports: [5173], turnStartMs: TWO_MIN_AGO }),
    agentState: "working",
    activityDetail: "editing live-updates.ts",
    branch: "main",
    ports: [5173],
    notification: null,
    paneCount: 1,
    waitingCount: 0,
    workingCount: 1,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: TWO_MIN_AGO,
    doneAtMs: 0,
    lastActivity: "now",
  },
  // Idle / done, multi-pane — sidebar shows "+142 −38 · ⎇ main ↑2" + breakdown.
  {
    id: "s-idle",
    title: "product-tools-prod",
    shell: "pwsh",
    projectId: "",
    worktreeBranch: "",
    rootPane: {
      kind: "split",
      id: "split-1",
      orientation: "v",
      children: [
        leaf({ name: "bq-query-monitor", agentState: "done", branch: "main", commitCount: 2, linesAdded: 90, linesDeleted: 20, filesChanged: 4, ahead: 2, doneAtMs: FOUR_MIN_AGO }),
        leaf({ name: "nadec updates", agentState: "done", branch: "main", commitCount: 1, linesAdded: 52, linesDeleted: 18, filesChanged: 3, ahead: 2, doneAtMs: Date.now() - 600_000 }),
        leaf({ name: "cohort costs", agentState: "working", activityDetail: "using Bash", branch: "main" }),
      ],
    },
    agentState: "done",
    activityDetail: "",
    branch: "main",
    ports: [],
    notification: null,
    paneCount: 3,
    waitingCount: 0,
    workingCount: 1,
    linesAdded: 142,
    linesDeleted: 38,
    filesChanged: 7,
    ahead: 2,
    turnStartMs: 0,
    doneAtMs: FOUR_MIN_AGO,
    lastActivity: "4m ago",
  },
  // Dormant idle — sidebar shows "⎇ main · :3000".
  {
    id: "s-dormant",
    title: "bq-query-monitor",
    shell: "pwsh",
    projectId: "",
    worktreeBranch: "",
    rootPane: leaf({ name: "shell", agentState: "idle", branch: "main", ports: [3000] }),
    agentState: "idle",
    activityDetail: "",
    branch: "main",
    ports: [3000],
    notification: null,
    paneCount: 1,
    waitingCount: 0,
    workingCount: 0,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    lastActivity: "2h ago",
  },
  // Permission — the genuine "Needs you" with an ask note.
  {
    id: "s-perm",
    title: "infra deploy",
    shell: "pwsh",
    projectId: "",
    worktreeBranch: "",
    rootPane: leaf({ name: "deploy", agentState: "permission", branch: "main", notification: { text: "Allow running `terraform apply` in prod?", level: "error" } }),
    agentState: "permission",
    activityDetail: "",
    branch: "main",
    ports: [],
    notification: { text: "Allow running `terraform apply` in prod?", level: "error" },
    paneCount: 1,
    waitingCount: 1,
    workingCount: 0,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    lastActivity: "now",
  },
];

const view = location.hash.replace("#", "") || "sidebar";

// Live tickers, same as main.ts — the harness should breathe like the app
// (elapsed labels count, working spinners spin) so captures look real.
startElapsedTicker();
startSpinnerTicker();

// #projects — project mode: grouped tabs with the COMPACT 2-line rows (title +
// one ellipsized meta line: ⏱ time-since-finish, per-tab loc, branch, panes).
// Mirrors the shared-tree scenario the attribution work fixed: two done tabs in
// one repo, each wearing ITS OWN +A −D instead of both wearing the union.
//
// The p-ptp tabs all sit on `main` in ONE repo, so they MUST all carry the same
// ahead — `@{upstream}..HEAD` is a fact about the branch, and no two of them can
// disagree about it. (The fixture used to give one tab ↑2 and its same-branch
// siblings ↑0, which git can't produce.) Held honest here because it's also the
// case that inflated the group header: summing ↑6 per-tab read ↑18.
const projectsList = [
  { id: "p-ptp", name: "product-tools-prod", path: "C:\\dev\\product-tools-prod" },
  { id: "p-gm", name: "global-models", path: "C:\\dev\\global-models" },
];
function projectTab(over: Partial<SessionView> & { id: string; title: string }): SessionView {
  return {
    shell: "pwsh",
    projectId: "p-ptp",
    worktreeBranch: "",
    rootPane: leaf({ name: over.title, agentState: over.agentState ?? "done", branch: "main" }),
    agentState: "done",
    activityDetail: "",
    branch: "main",
    ports: [],
    notification: null,
    paneCount: 1,
    waitingCount: 0,
    workingCount: 0,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    lastActivity: "now",
    ...over,
  };
}
const projectSessions: SessionView[] = [
  projectTab({
    id: "s-tab1", title: "coldstart auds",
    // colorIndex still set on fixtures (the pane header shows it); the
    // sidebar row deliberately renders no color mark — see renderItem.
    rootPane: leaf({ name: "coldstart auds", agentState: "done", branch: "main", colorIndex: 5 }),
    doneAtMs: Date.now() - 47_000, linesAdded: 143, linesDeleted: 131, filesChanged: 6,
    ahead: 6,
  }),
  // The reported mixed-tab scenario: aggregate state is "done" (host ranks
  // Done above Working) but ONE of the two panes is still working — the row
  // must spin with a right-edge elapsed, not read "finished". turnStartMs is
  // projected from the working pane, exactly as the host does.
  projectTab({
    id: "s-tab2", title: "generate tt/b4rec",
    rootPane: leaf({ name: "generate tt/b4rec", agentState: "done", branch: "main", colorIndex: 1 }),
    doneAtMs: Date.now() - 31_000, linesAdded: 89, linesDeleted: 12, filesChanged: 3,
    paneCount: 2, ahead: 6, workingCount: 1, turnStartMs: TWO_MIN_AGO,
  }),
  projectTab({
    id: "s-tab3", title: "fix perms audit",
    rootPane: leaf({ name: "fix perms audit", agentState: "working", activityDetail: "editing hook.ts", branch: "main", ports: [5173] }),
    agentState: "working", activityDetail: "editing hook.ts", ports: [5173],
    workingCount: 1, turnStartMs: TWO_MIN_AGO, doneAtMs: 0, ahead: 6,
  }),
  // Long tagged title: proves the pre-title tag line survives the ellipsis
  // (the old trailing dot was swallowed with the clipped text).
  projectTab({
    id: "s-tab4", title: "etl backfill for the q3 revenue attribution model", projectId: "p-gm",
    rootPane: leaf({ name: "etl backfill", agentState: "done", branch: "dag-fixes", colorIndex: 3 }),
    branch: "dag-fixes", doneAtMs: FOUR_MIN_AGO, linesAdded: 52, linesDeleted: 18, filesChanged: 3,
  }),
];

// #warmth — the age ramp on "your turn" rows. A `done` tab means the agent
// handed the turn back, so all of these are actionable; what separates them is
// AGE, and the ramp spans an hour. Staggering doneAtMs across the four buckets
// is the only way to see the whole thing at once (waiting it out would take an
// hour of wall clock, and the buckets themselves are unit-tested in
// warmth.test.ts — what needs EYES is whether the ramp reads).
//
// s-w-hot is the ACTIVE row on purpose: the active row's close ✕ permanently
// owns the chip cell, so its age has to route to the meta line instead. That
// path shipped broken in the first cut and is invisible in any fixture that
// only looks at inactive rows.
const MIN_MS = 60_000;
const warmthSessions: SessionView[] = [
  projectTab({
    id: "s-w-hot", title: "cleanup ds2 gcs",
    doneAtMs: Date.now() - 12_000, linesAdded: 143, linesDeleted: 131, filesChanged: 6, ahead: 6,
  }),
  projectTab({
    id: "s-w-hot2", title: "fc-double-check",
    doneAtMs: Date.now() - 40_000, linesAdded: 12, linesDeleted: 4, filesChanged: 2, ahead: 6,
  }),
  projectTab({
    id: "s-w-warm", title: "ds-coverage pinstall",
    doneAtMs: Date.now() - 4 * MIN_MS, linesAdded: 89, linesDeleted: 12, filesChanged: 3, ahead: 6,
  }),
  // A working tab in the middle, so the ramp is seen next to the state it must
  // NOT disturb: the braille spinner and its own elapsed are untouched by this.
  projectTab({
    id: "s-w-working", title: "ccs metrics",
    rootPane: leaf({ name: "ccs metrics", agentState: "working", activityDetail: "running bq", branch: "main" }),
    agentState: "working", activityDetail: "running bq",
    workingCount: 1, turnStartMs: TWO_MIN_AGO, doneAtMs: 0, ahead: 6,
  }),
  projectTab({
    id: "s-w-cool", title: "faux-targets-check",
    doneAtMs: Date.now() - 22 * MIN_MS, linesAdded: 30, linesDeleted: 9, filesChanged: 2, ahead: 6,
  }),
  projectTab({
    id: "s-w-cold", title: "coldstart - custom M",
    doneAtMs: Date.now() - 2 * 3_600_000, linesAdded: 4, linesDeleted: 1, filesChanged: 1, ahead: 6,
  }),
  projectTab({
    id: "s-w-cold2", title: "embeddings sync", projectId: "p-gm",
    doneAtMs: Date.now() - 5 * 3_600_000, linesAdded: 2, linesDeleted: 0, filesChanged: 1,
  }),
];

const list = document.getElementById("sidebar-scroll")!;
const newBtn = document.getElementById("new-session-button")!;
// "Recently closed" container — present in the real index.html; create a
// fallback for the standalone harness page and pin it above the footer.
let closedEl = document.getElementById("recently-closed");
if (!closedEl) {
  closedEl = document.createElement("div");
  closedEl.id = "recently-closed";
  closedEl.className = "recently-closed";
  list.parentElement?.insertBefore(closedEl, list.nextSibling);
}
const NOW = Date.now();
if (view === "warmth") {
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () => sb.render(warmthSessions, "s-w-hot", [], projectsList, "projects");
  sb.rerender();
} else if (view === "projects" || view === "projects-tip") {
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () => sb.render(projectSessions, "s-tab1", [], projectsList, "projects");
  sb.rerender();
  // #projects-tip: force the first inactive tab's hover-meta open (via the
  // --peek class) so the revealed metrics line shows in a headless capture,
  // where a real pointer can't hover.
  if (view === "projects-tip")
    setTimeout(() => {
      const rows = document.querySelectorAll<HTMLElement>(".session-item--nested");
      for (const r of rows) {
        if (!r.classList.contains("session-item--active")) { r.classList.add("session-item--peek"); break; }
      }
    }, 80);
} else {
  new Sidebar(list, newBtn, closedEl).render(sessions, "s-idle", [
    { id: "c-1", title: "kanban refactor", paneCount: 3, resumableCount: 2, closedAtMs: NOW - 5 * 60_000 },
    { id: "c-2", title: "docs site", paneCount: 1, resumableCount: 0, closedAtMs: NOW - 42 * 60_000 },
  ]);
}

const dash = new Dashboard(
  document.getElementById("dashboard")!,
  document.getElementById("dash-badge")!
);
dash.render(sessions);

// #modelmenu — the per-pane Claude model picker: mock pane headers wearing the
// quiet model chip, plus the flyout in both states (no usage data — the normal
// case — and Fable at its weekly limit, disabled with a reset hint). The left
// menu is a DOM clone of the real flyout (the module allows one live menu at a
// time); the right one is live, so opening this page in a browser is clickable.
if (view === "modelmenu") {
  const stage = document.getElementById("workspace")!;
  stage.style.cssText = "display:flex;gap:32px;padding:24px;align-items:flex-start;";
  const mk = (label: string, name: string, model: string) => {
    const wrap = document.createElement("div");
    wrap.style.cssText = "flex:1 1 0;font:11px var(--font-small,sans-serif);color:rgba(255,255,255,0.4)";
    const cap = document.createElement("div");
    cap.textContent = label;
    cap.style.cssText = "margin-bottom:4px";
    const pane = document.createElement("div");
    pane.className = "pane pane--active";
    pane.style.cssText = "height:96px";
    const header = buildPaneHeader("demo-" + name);
    header.nameEl.textContent = name;
    applyAgentBadge(header.agentBadgeEl, "claude");
    applyModelChip(header.modelEl, "claude", model);
    // applyLeafView hides these when empty in the real app; the mock skips it.
    header.branchEl.style.display = "none";
    header.commitsEl.style.display = "none";
    const term = document.createElement("div");
    term.className = "pane__term";
    term.style.cssText = "display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.25)";
    term.textContent = "terminal";
    pane.append(header.root, term);
    wrap.append(cap, pane);
    stage.appendChild(wrap);
    return header;
  };
  const a = mk("no usage data (the normal case)", "api refactor", "fable");
  const b = mk("fable at its weekly limit", "etl backfill", "");
  setTimeout(() => {
    showModelMenu(a.modelEl, "demo-a", "fable");
    const live = document.querySelector(".model-menu");
    if (live) {
      const clone = live.cloneNode(true) as HTMLElement;
      dismissModelMenu();
      document.body.appendChild(clone);
    }
    setModelLimits([{ alias: "fable", resetsAtMs: Date.now() + 9 * 3600_000 }]);
    showModelMenu(b.modelEl, "demo-b", "");
    (document.activeElement as HTMLElement | null)?.blur?.();
  }, 80);
}

// #paneactions — the new pane-header action buttons (split right / split down /
// open browser pane), sitting left of the ✕. The active pane reveals them (CSS
// gates on .pane--active / :hover); the browser button's URL-entry popover is
// opened so the whole feature is in one capture.
if (view === "paneactions") {
  const stage = document.getElementById("workspace")!;
  stage.style.cssText = "display:flex;flex-direction:column;gap:16px;padding:24px;";
  const mk = (label: string, name: string, active: boolean, agent: string | undefined) => {
    const wrap = document.createElement("div");
    wrap.style.cssText = "font:11px var(--font-small,sans-serif);color:rgba(255,255,255,0.4)";
    const cap = document.createElement("div");
    cap.textContent = label;
    cap.style.cssText = "margin-bottom:4px";
    const pane = document.createElement("div");
    pane.className = "pane" + (active ? " pane--active" : "");
    pane.style.cssText = "height:104px;width:380px";
    const header = buildPaneHeader("demo-" + name);
    header.nameEl.textContent = name;
    if (agent) applyAgentBadge(header.agentBadgeEl, agent);
    header.branchEl.style.display = "none";
    header.commitsEl.style.display = "none";
    const term = document.createElement("div");
    term.className = "pane__term";
    term.style.cssText = "display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.25)";
    term.textContent = "terminal";
    pane.append(header.root, term);
    wrap.append(cap, pane);
    stage.appendChild(wrap);
    return header;
  };
  const active = mk("active pane — actions revealed", "nadec-api", true, "claude");
  mk("inactive pane — actions hidden until hover", "cohort costs", false, "claude");
  // Open the browser-URL popover under the active pane's browser button.
  setTimeout(() => {
    const browserBtn = active.root.querySelector<HTMLElement>('.pane__action[data-action="browser"]');
    if (browserBtn) showBrowserPrompt(browserBtn, () => {});
  }, 80);
}

// #panefooter — mock .pane shells (no xterm) exercising every footer state, so
// the per-pane status bar can be screenshotted offline. Active panes show the
// focus-gated git stats; inactive ones don't.
if (view === "panefooter") {
  type Leaf = Extract<PaneTreeView, { kind: "leaf" }>;
  const cases: Array<{ label: string; leaf: Leaf; active: boolean }> = [
    { label: "working (active)", active: true,
      leaf: leaf({ name: "nadec-api", agentState: "working", activityDetail: "editing live-updates.ts", turnStartMs: TWO_MIN_AGO, linesAdded: 142, linesDeleted: 38, filesChanged: 7, ahead: 2, ports: [5173] }) },
    { label: "done (active)", active: true,
      leaf: leaf({ name: "cohort costs", agentState: "done", doneAtMs: FOUR_MIN_AGO, linesAdded: 90, linesDeleted: 20, filesChanged: 4, ahead: 2 }) },
    { label: "done (inactive — git stats hidden)", active: false,
      leaf: leaf({ name: "split sibling", agentState: "done", doneAtMs: FOUR_MIN_AGO, linesAdded: 90, linesDeleted: 20, filesChanged: 4, ahead: 2 }) },
    { label: "permission", active: true,
      leaf: leaf({ name: "infra deploy", agentState: "permission" }) },
    { label: "idle shell with dev server", active: false,
      leaf: leaf({ name: "shell", agentState: "idle", ports: [3000] }) },
    { label: "idle shell, nothing (footer collapses)", active: false,
      leaf: leaf({ name: "shell", agentState: "idle" }) },
  ];
  const stage = document.getElementById("workspace")!;
  stage.style.cssText = "display:flex;flex-direction:column;gap:12px;padding:16px;";
  for (const c of cases) {
    const wrap = document.createElement("div");
    wrap.style.cssText = "font:11px var(--font-small,sans-serif);color:rgba(255,255,255,0.4)";
    const cap = document.createElement("div");
    cap.textContent = c.label;
    cap.style.cssText = "margin-bottom:4px";
    const pane = document.createElement("div");
    pane.className = "pane" + (c.active ? " pane--active" : "");
    pane.style.cssText = "height:84px";
    const term = document.createElement("div");
    term.className = "pane__term";
    term.style.cssText = "display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.25)";
    term.textContent = "terminal";
    const footer = buildPaneFooter();
    applyPaneFooter(footer, c.leaf, c.active);
    pane.append(term, footer.root);
    wrap.append(cap, pane);
    stage.appendChild(wrap);
  }
}

// #paneheader — the header identity strip, exercising the NEW neutral ports
// chip (⊙ :port) next to branch/commits, across color tags and port counts.
if (view === "paneheader") {
  type Leaf = Extract<PaneTreeView, { kind: "leaf" }>;
  const cases: Array<{ label: string; leaf: Leaf; active: boolean }> = [
    { label: "claude · orange tag · one port · working", active: true,
      leaf: leaf({ name: "web-ui", colorIndex: 3, agentType: "claude", model: "", agentState: "working", branch: "main", ports: [5173] }) },
    { label: "claude · green tag · two ports · done", active: false,
      leaf: leaf({ name: "payments-api", colorIndex: 1, agentType: "claude", model: "opus", agentState: "done", branch: "feat/pay", commitCount: 2, ports: [8000, 8001] }) },
    { label: "plain shell · one port · idle", active: false,
      leaf: leaf({ name: "storybook", colorIndex: 5, agentState: "idle", branch: "main", ports: [6006] }) },
    { label: "no server (control — chip hidden)", active: false,
      leaf: leaf({ name: "docs", colorIndex: 0, agentType: "claude", model: "", agentState: "idle", branch: "main" }) },
  ];
  const stage = document.getElementById("workspace")!;
  stage.style.cssText = "display:flex;flex-direction:column;gap:12px;padding:16px;";
  for (const c of cases) {
    const wrap = document.createElement("div");
    wrap.style.cssText = "font:11px var(--font-small,sans-serif);color:rgba(255,255,255,0.4)";
    const cap = document.createElement("div");
    cap.textContent = c.label;
    cap.style.cssText = "margin-bottom:4px";
    const pane = document.createElement("div");
    pane.className = "pane" + (c.active ? " pane--active" : "");
    pane.dataset.color = String(c.leaf.colorIndex);
    pane.dataset.state = c.leaf.agentState;
    pane.style.width = "760px";
    const h = buildPaneHeader(c.leaf.paneId);
    h.colorDotEl.dataset.color = String(c.leaf.colorIndex);
    h.nameEl.textContent = c.leaf.name;
    applyChips(h.branchEl, h.commitsEl, c.leaf, c.active);
    applyPorts(h.portsEl, c.leaf);
    applyAgentBadge(h.agentBadgeEl, c.leaf.agentType);
    applyModelChip(h.modelEl, c.leaf.agentType, c.leaf.model);
    h.stateDotEl.dataset.state = c.leaf.agentState;
    h.stateLabelEl.textContent =
      c.leaf.agentState === "idle" ? "" :
      c.leaf.agentState === "done" ? "idle" : c.leaf.agentState;
    const term = document.createElement("div");
    term.className = "pane__term";
    term.style.cssText = "height:60px;display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.25)";
    term.textContent = "terminal";
    pane.append(h.root, term);
    wrap.append(cap, pane);
    stage.appendChild(wrap);
  }
}

// #newtab — the "New tab in <project>" dialog with the creation-time model
// field: Claude preselected (so the Model row is visible), Default checked,
// and Fable at its weekly limit so the disabled segment + its "resets HH:MM"
// hint line show in a capture. Limits must be set BEFORE the dialog opens —
// the field reads them at build time, same as the flyout reads them at open.
if (view === "newtab" || view === "newtab-browser") {
  setModelLimits([{ alias: "fable", resetsAtMs: Date.now() + 9 * 3600_000 }]);
  showNewTabDialog({
    id: "p-ptp",
    name: "product-tools-prod",
    path: "C:\\dev\\product-tools-prod",
  });
  // #newtab-browser — pick the Browser segment so the Address field shows and
  // the worktree/model rows hide (a real pointer can't click in a capture).
  if (view === "newtab-browser") {
    setTimeout(() => {
      const btns = Array.from(document.querySelectorAll<HTMLElement>(".newtab-seg__btn"));
      btns.find((b) => b.textContent === "Browser")?.click();
    }, 60);
  }
}

if (view === "panechooser") {
  // #panechooser — a mock .pane (no xterm) with the in-pane new-pane chooser
  // overlaid, so the centered dialog can be screenshotted offline. The send()
  // on a pick is a no-op in the harness (no chrome.webview), and we don't click
  // anyway — we just render it for the capture.
  const stage = document.getElementById("workspace")!;
  // The real .workspace is a flex row; force block so the standalone .pane
  // fills the width instead of shrinking to its content (harness-only).
  stage.style.cssText = "padding:16px;height:100%;box-sizing:border-box;display:block;";
  const pane = document.createElement("div");
  pane.className = "pane pane--active";
  pane.style.cssText = "height:100%;width:100%;box-sizing:border-box;";
  const term = document.createElement("div");
  term.className = "pane__term";
  term.style.cssText = "display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,0.18);font:13px var(--font-mono,monospace);";
  term.textContent = "terminal";
  pane.appendChild(term);
  stage.appendChild(pane);
  void showPaneChooser(pane, {
    cwd: "C:\\Users\\irisy\\dev-projects\\perch",
    agentType: "claude",
    defaultCwd: "C:\\Users\\irisy",
  });
}

// #commits / #commits-lightbox — the "ready to push" recap, driven by the
// dev host stub in harness.html (answers commits.request with sample data).
if (view === "commits") {
  const anchor = document.querySelector<HTMLElement>(".session-item__meta-item--ahead");
  if (anchor) openCommitsPopover(anchor, "demo-pane");
} else if (view === "commits-lightbox") {
  openCommitsLightbox("demo-pane");
}

if (view === "dashboard") {
  dash.show();
} else if (view === "confirm") {
  void confirmDialog({
    title: "Restore kanban refactor?",
    body: "Reopen this session's 3 panes and resume 2 Claude sessions.",
    confirmLabel: "Restore",
    cancelLabel: "Cancel",
  });
} else if (view === "restore") {
  // Restore-progress lightbox mid-flight: one pane resumed, one still spinning.
  const rp = new RestoreProgress();
  rp.begin([
    { paneId: "p1", name: "nadec-api", sessionTitle: "kanban refactor" },
    { paneId: "p2", name: "kanban-ui", sessionTitle: "kanban refactor" },
    { paneId: "p3", name: "docs", sessionTitle: "kanban refactor" },
  ]);
  rp.progress("p1", "ready");
  rp.progress("p2", "resuming");
} else if (view === "cloud-sidebar" || view === "cloud-sidebar-calm") {
  // #cloud-sidebar      — the CLOUD area with an orphan: escalated to caution,
  //                       and grown an extra row.
  // #cloud-sidebar-calm — everything attributed: the teal resting state. Note it
  //                       is still COLORED, because the area existing at all
  //                       means a machine is billing you.
  const HOUR = 3_600_000;
  const now = Date.now();
  const base = {
    kind: "instance" as const,
    zone: "us-central1-a",
    vmCount: 1,
    priceKnown: true,
    paneId: "3a91",
    task: "Sweep learning rates for the v3 global model",
    startedByPerch: true,
  };
  const orphan = {
    ...base,
    id: "us-central1-a/gpu-train-h1",
    name: "gpu-train-h1",
    machineType: "a2-highgpu-1g",
    isGpu: true,
    createdMs: now - 52 * HOUR,
    usdPerHour: 3.6733,
    agentName: "train-sweep",
    isOrphan: true,
    agentState: null,
  };
  const live = {
    ...base,
    id: "cluster/dp-lookalike-3a91",
    name: "dp-lookalike-3a91",
    kind: "cluster" as const,
    machineType: "n2-standard-8",
    vmCount: 5,
    isGpu: false,
    createdMs: now - 4 * HOUR,
    usdPerHour: 3.8,
    agentName: "audience-builder",
    isOrphan: false,
    agentState: "working",
    task: "Build a lookalike audience for the retail campaign",
  };
  applyCloudData({
    type: "cloud.data",
    nowMs: now,
    resources: view === "cloud-sidebar-calm" ? [live] : [orphan, live],
  });
} else if (view === "cloud") {
  // #cloud — the cloud panel with the four shapes that actually matter:
  //   1. an orphaned GPU box (the expensive mistake this whole feature exists for)
  //   2. an orphaned Dataproc cluster (one row, not N — and a different delete cmd)
  //   3. a live cluster whose agent is still working
  //   4. two machines from ONE agent, so the repeated "why" line is visible
  // Plus a machine with no price on file, which must render "—" and never $0.00.
  const HOUR = 3_600_000;
  const now = Date.now();
  showCloudPanel();
  applyCloudData({
    type: "cloud.data",
    nowMs: now,
    resources: [
      {
        id: "us-central1-a/gpu-train-h1",
        startedByPerch: true,
        name: "gpu-train-h1",
        kind: "instance",
        machineType: "a2-highgpu-1g",
        zone: "us-central1-a",
        vmCount: 1,
        isGpu: true,
        createdMs: now - 52 * HOUR,
        usdPerHour: 3.6733,
        priceKnown: true,
        agentName: "train-sweep",
        task: "Sweep learning rates for the v3 global model, 10M rows",
        paneId: "9f2c",
        isOrphan: true,
        agentState: null,
      },
      {
        id: "cluster/dp-audience-8f2c",
        startedByPerch: true,
        name: "dp-audience-8f2c",
        kind: "cluster",
        machineType: "e2-standard-8",
        zone: "us-east5-c",
        vmCount: 3,
        isGpu: false,
        createdMs: now - 14.4 * HOUR,
        usdPerHour: 1.89,
        priceKnown: true,
        agentName: "audience-builder",
        task: "Build the retail blacklist audience from the seed list",
        paneId: "3a91",
        isOrphan: true,
        agentState: null,
      },
      {
        id: "cluster/dp-lookalike-3a91",
        startedByPerch: true,
        name: "dp-lookalike-3a91",
        kind: "cluster",
        machineType: "n2-standard-8",
        zone: "us-east5-c",
        vmCount: 5,
        isGpu: false,
        createdMs: now - 0.63 * HOUR,
        usdPerHour: 3.8,
        priceKnown: true,
        agentName: "audience-builder",
        task: "Build a lookalike audience for the retail campaign, TFIDF, US only",
        paneId: "3a91",
        isOrphan: false,
        agentState: "working",
      },
      {
        id: "us-central1-a/gpu-eval-01",
        startedByPerch: true,
        name: "gpu-eval-01",
        kind: "instance",
        machineType: "a2-highgpu-1g",
        zone: "us-central1-a",
        vmCount: 1,
        isGpu: true,
        createdMs: now - 1.2 * HOUR,
        usdPerHour: 3.6733,
        priceKnown: true,
        agentName: "offline-eval",
        task: "Run the offline candidate eval for the shopping app vs the July model",
        paneId: "5b7d",
        isOrphan: false,
        agentState: "done",
      },
      {
        id: "us-east5-b/exotic-1",
        startedByPerch: true,
        name: "exotic-1",
        kind: "instance",
        machineType: "c4-hypernova-99",
        zone: "us-east5-b",
        vmCount: 1,
        isGpu: false,
        createdMs: now - 2 * HOUR,
        usdPerHour: 0,
        priceKnown: false,
        agentName: "offline-eval",
        task: "Run the offline candidate eval for the shopping app vs the July model",
        paneId: "5b7d",
        isOrphan: false,
        agentState: "done",
      },
      {
        // GPU radar: a Terraform-provisioned A100 box nobody here started. View-
        // only — no agent, no kill, but costed so it can't hide.
        id: "us-central1-c/ds-ml-dws",
        startedByPerch: false,
        name: "ds-ml-dws-us-central1-c-l0jl",
        kind: "instance",
        machineType: "a2-ultragpu-4g",
        zone: "us-central1-c",
        vmCount: 1,
        isGpu: true,
        createdMs: now - 2.1 * HOUR,
        usdPerHour: 20.2752,
        priceKnown: true,
        agentName: null,
        task: null,
        paneId: null,
        isOrphan: false,
        agentState: null,
      },
    ],
  });
}
