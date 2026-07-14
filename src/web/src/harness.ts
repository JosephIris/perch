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
import { buildPaneHeader, applyModelChip, applyAgentBadge } from "./pane-header.js";
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
    title: "storefront live preview",
    shell: "pwsh",
    projectId: "",
    worktreeBranch: "",
    rootPane: leaf({ name: "storefront-api", agentState: "working", activityDetail: "editing live-preview.ts", branch: "main", ports: [5173], turnStartMs: TWO_MIN_AGO }),
    agentState: "working",
    activityDetail: "editing live-preview.ts",
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
    title: "storefront-web",
    shell: "pwsh",
    projectId: "",
    worktreeBranch: "",
    rootPane: {
      kind: "split",
      id: "split-1",
      orientation: "v",
      children: [
        leaf({ name: "log-viewer", agentState: "done", branch: "main", commitCount: 2, linesAdded: 90, linesDeleted: 20, filesChanged: 4, ahead: 2, doneAtMs: FOUR_MIN_AGO }),
        leaf({ name: "changelog update", agentState: "done", branch: "main", commitCount: 1, linesAdded: 52, linesDeleted: 18, filesChanged: 3, ahead: 2, doneAtMs: Date.now() - 600_000 }),
        leaf({ name: "usage report", agentState: "working", activityDetail: "using Bash", branch: "main" }),
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
    title: "log-viewer",
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
const projectsList = [
  { id: "p-ptp", name: "storefront-web", path: "C:\\dev\\storefront-web" },
  { id: "p-gm", name: "home-tools", path: "C:\\dev\\home-tools" },
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
    id: "s-tab1", title: "signup flow",
    // colorIndex still set on fixtures (the pane header shows it); the
    // sidebar row deliberately renders no color mark — see renderItem.
    rootPane: leaf({ name: "signup flow", agentState: "done", branch: "main", colorIndex: 5 }),
    doneAtMs: Date.now() - 47_000, linesAdded: 143, linesDeleted: 131, filesChanged: 6,
  }),
  // The reported mixed-tab scenario: aggregate state is "done" (host ranks
  // Done above Working) but ONE of the two panes is still working — the row
  // must spin with a right-edge elapsed, not read "finished". turnStartMs is
  // projected from the working pane, exactly as the host does.
  projectTab({
    id: "s-tab2", title: "generate sitemap",
    rootPane: leaf({ name: "generate sitemap", agentState: "done", branch: "main", colorIndex: 1 }),
    doneAtMs: Date.now() - 31_000, linesAdded: 89, linesDeleted: 12, filesChanged: 3,
    paneCount: 2, ahead: 2, workingCount: 1, turnStartMs: TWO_MIN_AGO,
  }),
  projectTab({
    id: "s-tab3", title: "fix perms audit",
    rootPane: leaf({ name: "fix perms audit", agentState: "working", activityDetail: "editing hook.ts", branch: "main" }),
    agentState: "working", activityDetail: "editing hook.ts",
    workingCount: 1, turnStartMs: TWO_MIN_AGO, doneAtMs: 0,
  }),
  // Long tagged title: proves the pre-title tag line survives the ellipsis
  // (the old trailing dot was swallowed with the clipped text).
  projectTab({
    id: "s-tab4", title: "backfill the q3 sales report", projectId: "p-gm",
    rootPane: leaf({ name: "etl backfill", agentState: "done", branch: "dag-fixes", colorIndex: 3 }),
    branch: "dag-fixes", doneAtMs: FOUR_MIN_AGO, linesAdded: 52, linesDeleted: 18, filesChanged: 3,
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
if (view === "projects" || view === "projects-tip") {
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () => sb.render(projectSessions, "s-tab1", [], projectsList, "projects");
  sb.rerender();
  // #projects-tip: hold the first inactive tab's ⓘ "hovered" so the metrics
  // tooltip is up when the screenshot fires (a real pointer can't hover in a
  // headless capture).
  if (view === "projects-tip")
    setTimeout(() => {
      document
        .querySelector(".session-item__info")
        ?.dispatchEvent(new MouseEvent("mouseenter"));
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

// #panefooter — mock .pane shells (no xterm) exercising every footer state, so
// the per-pane status bar can be screenshotted offline. Active panes show the
// focus-gated git stats; inactive ones don't.
if (view === "panefooter") {
  type Leaf = Extract<PaneTreeView, { kind: "leaf" }>;
  const cases: Array<{ label: string; leaf: Leaf; active: boolean }> = [
    { label: "working (active)", active: true,
      leaf: leaf({ name: "storefront-api", agentState: "working", activityDetail: "editing live-preview.ts", turnStartMs: TWO_MIN_AGO, linesAdded: 142, linesDeleted: 38, filesChanged: 7, ahead: 2, ports: [5173] }) },
    { label: "done (active)", active: true,
      leaf: leaf({ name: "usage report", agentState: "done", doneAtMs: FOUR_MIN_AGO, linesAdded: 90, linesDeleted: 20, filesChanged: 4, ahead: 2 }) },
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

// #newtab — the "New tab in <project>" dialog with the creation-time model
// field: Claude preselected (so the Model row is visible), Default checked,
// and Fable at its weekly limit so the disabled segment + its "resets HH:MM"
// hint line show in a capture. Limits must be set BEFORE the dialog opens —
// the field reads them at build time, same as the flyout reads them at open.
if (view === "newtab") {
  setModelLimits([{ alias: "fable", resetsAtMs: Date.now() + 9 * 3600_000 }]);
  showNewTabDialog({
    id: "p-ptp",
    name: "storefront-web",
    path: "C:\\dev\\storefront-web",
  });
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
    cwd: "C:\\Users\\dev\\dev-projects\\perch",
    agentType: "claude",
    defaultCwd: "C:\\Users\\dev",
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
    { paneId: "p1", name: "storefront-api", sessionTitle: "kanban refactor" },
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
    task: "Rebuild the search index for the v3 catalog",
  };
  const orphan = {
    ...base,
    id: "us-central1-a/build-runner-h1",
    name: "build-runner-h1",
    machineType: "a2-highgpu-1g",
    isGpu: true,
    createdMs: now - 52 * HOUR,
    usdPerHour: 3.6733,
    agentName: "build-sweep",
    isOrphan: true,
    agentState: null,
  };
  const live = {
    ...base,
    id: "cluster/batch-web-3a91",
    name: "batch-web-3a91",
    kind: "cluster" as const,
    machineType: "n2-standard-8",
    vmCount: 5,
    isGpu: false,
    createdMs: now - 4 * HOUR,
    usdPerHour: 3.8,
    agentName: "page-builder",
    isOrphan: false,
    agentState: "working",
    task: "Resize product images to WebP for the summer sale",
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
        id: "us-central1-a/build-runner-h1",
        name: "build-runner-h1",
        kind: "instance",
        machineType: "a2-highgpu-1g",
        zone: "us-central1-a",
        vmCount: 1,
        isGpu: true,
        createdMs: now - 52 * HOUR,
        usdPerHour: 3.6733,
        priceKnown: true,
        agentName: "build-sweep",
        task: "Rebuild the search index for the v3 catalog, 10M rows",
        paneId: "9f2c",
        isOrphan: true,
        agentState: null,
      },
      {
        id: "cluster/batch-web-8f2c",
        name: "batch-web-8f2c",
        kind: "cluster",
        machineType: "e2-standard-8",
        zone: "us-east5-c",
        vmCount: 3,
        isGpu: false,
        createdMs: now - 14.4 * HOUR,
        usdPerHour: 1.89,
        priceKnown: true,
        agentName: "page-builder",
        task: "Rebuild the product catalog from the export file",
        paneId: "3a91",
        isOrphan: true,
        agentState: null,
      },
      {
        id: "cluster/batch-web-3a91",
        name: "batch-web-3a91",
        kind: "cluster",
        machineType: "n2-standard-8",
        zone: "us-east5-c",
        vmCount: 5,
        isGpu: false,
        createdMs: now - 0.63 * HOUR,
        usdPerHour: 3.8,
        priceKnown: true,
        agentName: "page-builder",
        task: "Resize product images to WebP for the summer sale, US only",
        paneId: "3a91",
        isOrphan: false,
        agentState: "working",
      },
      {
        id: "us-central1-a/test-runner-01",
        name: "test-runner-01",
        kind: "instance",
        machineType: "a2-highgpu-1g",
        zone: "us-central1-a",
        vmCount: 1,
        isGpu: true,
        createdMs: now - 1.2 * HOUR,
        usdPerHour: 3.6733,
        priceKnown: true,
        agentName: "nightly-report",
        task: "Run the nightly report for the shopping app vs the July build",
        paneId: "5b7d",
        isOrphan: false,
        agentState: "done",
      },
      {
        id: "us-east5-b/exotic-1",
        name: "exotic-1",
        kind: "instance",
        machineType: "c4-hypernova-99",
        zone: "us-east5-b",
        vmCount: 1,
        isGpu: false,
        createdMs: now - 2 * HOUR,
        usdPerHour: 0,
        priceKnown: false,
        agentName: "nightly-report",
        task: "Run the nightly report for the shopping app vs the July build",
        paneId: "5b7d",
        isOrphan: false,
        agentState: "done",
      },
    ],
  });
}
