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
import { Sidebar } from "./sidebar.js";
import { Dashboard } from "./dashboard.js";
import { confirmDialog } from "./confirm.js";
import { showPaneChooser } from "./pane-chooser.js";
import { buildPaneFooter, applyPaneFooter } from "./pane-footer.js";
import { RestoreProgress } from "./restore-progress.js";
import { openCommitsPopover, openCommitsLightbox } from "./commits-view.js";
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
new Sidebar(list, newBtn, closedEl).render(sessions, "s-idle", [
  { id: "c-1", title: "kanban refactor", paneCount: 3, resumableCount: 2, closedAtMs: NOW - 5 * 60_000 },
  { id: "c-2", title: "docs site", paneCount: 1, resumableCount: 0, closedAtMs: NOW - 42 * 60_000 },
]);

const dash = new Dashboard(
  document.getElementById("dashboard")!,
  document.getElementById("dash-badge")!
);
dash.render(sessions);

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
}
