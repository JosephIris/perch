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
    ...over,
  };
}

// Sample "working since 2m ago" for the live-elapsed label.
const TWO_MIN_AGO = Date.now() - 125_000;

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
        leaf({ name: "bq-query-monitor", agentState: "done", branch: "main", commitCount: 2, linesAdded: 90, linesDeleted: 20, filesChanged: 4, ahead: 2 }),
        leaf({ name: "nadec updates", agentState: "done", branch: "main", commitCount: 1, linesAdded: 52, linesDeleted: 18, filesChanged: 3, ahead: 2 }),
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
    lastActivity: "now",
  },
];

const view = location.hash.replace("#", "") || "sidebar";

const list = document.getElementById("sidebar-scroll")!;
const newBtn = document.getElementById("new-session-button")!;
new Sidebar(list, newBtn).render(sessions, "s-idle");

const dash = new Dashboard(
  document.getElementById("dashboard")!,
  document.getElementById("dash-badge")!
);
dash.render(sessions);

if (view === "dashboard") {
  dash.show();
} else if (view === "confirm") {
  void confirmDialog({
    title: "Close product-tools-prod?",
    body: "This closes the session and its 3 panes. The pane layout can't be recovered.",
    confirmLabel: "Close session",
    cancelLabel: "Keep open",
    danger: true,
  });
}
