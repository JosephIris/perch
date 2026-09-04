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
import type { SessionView, PaneTreeView, ProjectView, StateMessage, TeamDataMessage, TeamEntryView } from "./bridge.js";
import { openTeamRoom, applyTeamState, onTeamRoomChange, feedTeamFixture, applyArtefact, applyArtefactIndex } from "./team-room.js";
import { showNewBotDialog, applyBriefProgress, applyBriefResult, applyReferencePicked } from "./new-bot-dialog.js";
import { onMessage as onHostMessage } from "./bridge.js";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { createSetupOverlay } from "./setup-overlay.js";
import { initInspector } from "./inspector.js";
import { createBotFace, setFaceColorMode, faceColorMode, freezeFaces, FACE_HATS, FACE_EYEWEAR, FACE_EXTRAS, FACE_TEMPERS, FACE_STATES } from "./bot-face.js";
import type { BotLook, FaceHat, FaceState, FaceTemper } from "./bot-face.js";
import "@xterm/xterm/css/xterm.css";

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
    aheadMine: 0,
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
    boardPath: "",
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
    aheadMine: 0,
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
    boardPath: "",
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
    aheadMine: 0,
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
    boardPath: "",
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
    aheadMine: 0,
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
    boardPath: "",
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
    aheadMine: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    lastActivity: "now",
  },
];

// The view is the hash. A `?k=v` tail on it (or the page's own search string)
// carries view options — e.g. #botfaces?t=1200&color=1 — so a capture can pin
// a frame by number without a second HTML shell.
const hashRaw = location.hash.replace("#", "");
const hashQ = hashRaw.indexOf("?");
const view = (hashQ < 0 ? hashRaw : hashRaw.slice(0, hashQ)) || "sidebar";
const viewParams = new URLSearchParams(location.search);
if (hashQ >= 0) new URLSearchParams(hashRaw.slice(hashQ + 1)).forEach((v, k) => viewParams.set(k, v));

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
  { id: "p-ptp", name: "storefront-web", path: "C:\\dev\\storefront-web" },
  { id: "p-gm", name: "home-tools", path: "C:\\dev\\home-tools" },
];
function projectTab(over: Partial<SessionView> & { id: string; title: string }): SessionView {
  return {
    shell: "pwsh",
    projectId: "p-ptp",
    worktreeBranch: "",
    boardPath: "",
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
    aheadMine: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    lastActivity: "now",
    ...over,
  };
}
// The three p-ptp tabs share `main`, so all carry the BRANCH ahead of 6 (a
// git fact — see the shared-tree note above), but their hover chips show each
// tab's OWN unpushed commits (aheadMine): 3 + 2 + 1 = 6, no double counting,
// and no two tabs reading the same uninformative number.
const projectSessions: SessionView[] = [
  projectTab({
    id: "s-tab1", title: "signup flow",
    // colorIndex still set on fixtures (the pane header shows it); the
    // sidebar row deliberately renders no color mark — see renderItem.
    rootPane: leaf({ name: "signup flow", agentState: "done", branch: "main", colorIndex: 5, ahead: 6, aheadMine: 3 }),
    doneAtMs: Date.now() - 47_000, linesAdded: 143, linesDeleted: 131, filesChanged: 6,
    ahead: 6, aheadMine: 3,
  }),
  // The reported mixed-tab scenario: aggregate state is "done" (host ranks
  // Done above Working) but ONE of the two panes is still working — the row
  // must spin with a right-edge elapsed, not read "finished". turnStartMs is
  // projected from the working pane, exactly as the host does.
  projectTab({
    id: "s-tab2", title: "generate sitemap",
    rootPane: leaf({ name: "generate sitemap", agentState: "done", branch: "main", colorIndex: 1, ahead: 6, aheadMine: 2 }),
    doneAtMs: Date.now() - 31_000, linesAdded: 89, linesDeleted: 12, filesChanged: 3,
    paneCount: 2, ahead: 6, aheadMine: 2, workingCount: 1, turnStartMs: TWO_MIN_AGO,
  }),
  projectTab({
    id: "s-tab3", title: "fix perms audit",
    rootPane: leaf({ name: "fix perms audit", agentState: "working", activityDetail: "editing hook.ts", branch: "main", ports: [5173], ahead: 6, aheadMine: 1 }),
    agentState: "working", activityDetail: "editing hook.ts", ports: [5173],
    workingCount: 1, turnStartMs: TWO_MIN_AGO, doneAtMs: 0, ahead: 6, aheadMine: 1,
  }),
  // Permission-blocked tab in a NESTED row — the compact treatment: red dot +
  // a small caution "permission" tag on the right edge, ONE line (the two-line
  // note is sessions-mode only). The actual ask rides the tag's tooltip.
  projectTab({
    id: "s-tab-perm", title: "disc vm",
    rootPane: leaf({ name: "disc vm", agentState: "permission", branch: "main", notification: { text: "Allow running `gcloud compute instances delete`?", level: "error" } }),
    agentState: "permission", waitingCount: 1,
    notification: { text: "Allow running `gcloud compute instances delete`?", level: "error" },
  }),
  // Long tagged title: proves the pre-title tag line survives the ellipsis
  // (the old trailing dot was swallowed with the clipped text).
  projectTab({
    id: "s-tab4", title: "backfill the q3 sales report", projectId: "p-gm",
    rootPane: leaf({ name: "etl backfill", agentState: "done", branch: "dag-fixes", colorIndex: 3 }),
    branch: "dag-fixes", doneAtMs: FOUR_MIN_AGO, linesAdded: 52, linesDeleted: 18, filesChanged: 3,
  }),
  // Two SLEPT tabs (the moon button) — they file into the project's collapsed
  // "Idle" drawer instead of the active list, newest-slept first. Two of them
  // (not one) so the drawer's own tree connectors are visible: a trunk with an
  // elbow. The first one keeps agentState "done" ON PURPOSE: you usually sleep
  // a tab right after its agent hands the turn back, so the row arrives in the
  // drawer carrying a green dot. Dormant has to win over that — see the
  // [data-state="dormant"] rule in style.css.
  projectTab({
    id: "s-tab-sleep1", title: "cleanup ds2 gcs", dormant: true,
    rootPane: leaf({ name: "cleanup ds2 gcs", agentState: "done", branch: "main" }),
    agentState: "done", doneAtMs: 0, lastActivity: "3d ago",
  }),
  projectTab({
    id: "s-tab-sleep2", title: "non_att_today", dormant: true,
    rootPane: leaf({ name: "non_att_today", agentState: "idle", branch: "main" }),
    agentState: "idle", doneAtMs: 0, lastActivity: "4d ago",
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
    id: "s-w-hot", title: "cleanup temp uploads",
    doneAtMs: Date.now() - 12_000, linesAdded: 143, linesDeleted: 131, filesChanged: 6, ahead: 6,
  }),
  projectTab({
    id: "s-w-hot2", title: "form validation",
    doneAtMs: Date.now() - 40_000, linesAdded: 12, linesDeleted: 4, filesChanged: 2, ahead: 6,
  }),
  projectTab({
    id: "s-w-warm", title: "image optimizer",
    doneAtMs: Date.now() - 4 * MIN_MS, linesAdded: 89, linesDeleted: 12, filesChanged: 3, ahead: 6,
  }),
  // A working tab in the middle, so the ramp is seen next to the state it must
  // NOT disturb: the braille spinner and its own elapsed are untouched by this.
  projectTab({
    id: "s-w-working", title: "page metrics",
    rootPane: leaf({ name: "page metrics", agentState: "working", activityDetail: "running build", branch: "main" }),
    agentState: "working", activityDetail: "running build",
    workingCount: 1, turnStartMs: TWO_MIN_AGO, doneAtMs: 0, ahead: 6,
  }),
  projectTab({
    id: "s-w-cool", title: "broken link check",
    doneAtMs: Date.now() - 22 * MIN_MS, linesAdded: 30, linesDeleted: 9, filesChanged: 2, ahead: 6,
  }),
  projectTab({
    id: "s-w-cold", title: "onboarding emails",
    doneAtMs: Date.now() - 2 * 3_600_000, linesAdded: 4, linesDeleted: 1, filesChanged: 1, ahead: 6,
  }),
  projectTab({
    id: "s-w-cold2", title: "search reindex", projectId: "p-gm",
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

// #pair — cross-session pairing: two paired working tabs (bracket in the far-
// left gutter), one with an incoming "from …" note; a second scenario pairs
// across projects (no adjacency possible → link tags). #pair-hot forces the
// warm bracket the way live traffic would.
const pairSessions: SessionView[] = [
  projectTab({
    id: "s-pair-a", title: "user-profiles",
    rootPane: leaf({ name: "user-profiles", agentState: "working", activityDetail: "display-name migration", branch: "main" }),
    agentState: "working", activityDetail: "display-name migration",
    workingCount: 1, turnStartMs: NOW - 12 * 60_000, doneAtMs: 0,
    pairedWith: "s-pair-b",
  }),
  projectTab({
    id: "s-pair-b", title: "weekly-digest",
    rootPane: leaf({ name: "weekly-digest", agentState: "working", activityDetail: "writing weeklyDigest.ts", branch: "main" }),
    agentState: "working", activityDetail: "writing weeklyDigest.ts",
    workingCount: 1, turnStartMs: NOW - 4 * 60_000, doneAtMs: 0,
    pairedWith: "s-pair-a",
    pairNote: {
      from: "user-profiles",
      text: "users.name is now users.display_name, update the digest query",
      level: "info",
      atMs: NOW - 30_000,
    },
  }),
  projectTab({
    id: "s-pair-c", title: "docs sweep",
    doneAtMs: NOW - 2 * 3_600_000, linesAdded: 12, linesDeleted: 4, filesChanged: 2,
  }),
  // Cross-project pair: adjacency impossible, each row wears the link tag.
  projectTab({
    id: "s-pair-d", title: "landing copy", projectId: "p-gm",
    rootPane: leaf({ name: "landing copy", agentState: "working", activityDetail: "rewriting hero", branch: "site" }),
    agentState: "working", activityDetail: "rewriting hero", branch: "site",
    workingCount: 1, turnStartMs: NOW - 60_000, doneAtMs: 0,
    pairedWith: "s-pair-c",
  }),
];
// Make the cross-project pair mutual (c ↔ d) without re-declaring c.
pairSessions[2].pairedWith = "s-pair-d";

if (view === "warmth") {
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () => sb.render(warmthSessions, "s-w-hot", [], projectsList, "projects");
  sb.rerender();
} else if (view === "pair" || view === "pair-hot" || view === "pair-menu") {
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () => sb.render(pairSessions, "s-pair-a", [], projectsList, "projects");
  sb.rerender();
  // #pair-hot: live traffic — sender mid-send. Swap the activity detail to
  // the "messaging …" form the hook pushes; pairHot() then warms the bracket
  // on the NEXT render, exactly as a real push would.
  if (view === "pair-hot") {
    pairSessions[0].activityDetail = "messaging weekly-digest";
    const l = pairSessions[0].rootPane as Extract<PaneTreeView, { kind: "leaf" }>;
    l.activityDetail = "messaging weekly-digest";
    sb.rerender();
  }
  // #pair-menu: the right-click menu, opened the way a pointer would.
  if (view === "pair-menu")
    setTimeout(() => {
      document.querySelector<HTMLElement>(".session-item--nested")?.dispatchEvent(
        new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: 140, clientY: 120 }));
    }, 80);
} else if (view === "projects" || view === "projects-tip" || view === "projects-idle" ||
           view === "projects-sleep") {
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () => sb.render(projectSessions, "s-tab1", [], projectsList, "projects");
  sb.rerender();
  // #projects-idle: unfold the Idle drawer by CLICKING its head, not by poking
  // at Sidebar internals — a capture that bypasses the real toggle can't tell
  // you the toggle works. The drawer is shut on every launch by design, which
  // is exactly why a headless shot needs this.
  if (view === "projects-idle")
    setTimeout(() => document.querySelector<HTMLElement>(".idle-group__head")?.click(), 80);
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
  // #projects-sleep: peek the ACTIVE row. Its ✕ is permanent, so its age/tag
  // can't use the chip cell and rides the end of the title line instead —
  // right where the hovered moon paints itself. This is the one row where the
  // sleep button has something to collide with, and only a hover shows it.
  if (view === "projects-sleep")
    setTimeout(() => {
      document.querySelector<HTMLElement>(".session-item--active")
        ?.classList.add("session-item--peek");
    }, 80);
} else if (view === "hero") {
  // #hero — the README / marketing 16:9 shot, staged by design-loop/hero.html.
  // Same shape as the warmth fixtures (age ramp across two project groups),
  // but with FICTIONAL project/tab names: this image is public-facing, so it
  // must not leak anyone's real workspace. The active tab matches the panes
  // built below (CC working + a vite pane on :5173 + a booting pane).
  const heroProjects = [
    { id: "h-shop", name: "storefront-web", path: "C:\\dev\\storefront-web" },
    { id: "h-home", name: "home-tools", path: "C:\\dev\\home-tools" },
  ];
  const heroSessions: SessionView[] = [
    projectTab({
      id: "h-1", title: "holiday banner", projectId: "h-shop",
      rootPane: leaf({ name: "holiday banner", agentState: "working", branch: "main" }),
      agentState: "working", activityDetail: "adjusting the mobile breakpoint",
      paneCount: 3, workingCount: 1, ports: [5173], ahead: 3,
      turnStartMs: TWO_MIN_AGO, doneAtMs: 0,
    }),
    projectTab({
      id: "h-2", title: "fix checkout css", projectId: "h-shop",
      doneAtMs: Date.now() - 40_000, linesAdded: 61, linesDeleted: 17, filesChanged: 3, ahead: 3,
    }),
    projectTab({
      id: "h-3", title: "seo audit pass", projectId: "h-shop",
      doneAtMs: Date.now() - 4 * MIN_MS, linesAdded: 89, linesDeleted: 12, filesChanged: 3, ahead: 3,
    }),
    projectTab({
      id: "h-4", title: "cleanup old backups", projectId: "h-shop",
      rootPane: leaf({ name: "cleanup old backups", agentState: "working", activityDetail: "checking bucket references", branch: "main" }),
      agentState: "working", activityDetail: "checking bucket references",
      workingCount: 1, turnStartMs: TWO_MIN_AGO, doneAtMs: 0, ahead: 3,
    }),
    projectTab({
      id: "h-5", title: "invoice export", projectId: "h-shop",
      doneAtMs: Date.now() - 22 * MIN_MS, linesAdded: 30, linesDeleted: 9, filesChanged: 2, ahead: 3,
    }),
    projectTab({
      id: "h-6", title: "photo backup sync", projectId: "h-home",
      doneAtMs: Date.now() - 2 * 3_600_000, linesAdded: 4, linesDeleted: 1, filesChanged: 1,
    }),
    projectTab({
      id: "h-7", title: "recipe importer", projectId: "h-home",
      doneAtMs: Date.now() - 5 * 3_600_000, linesAdded: 2, linesDeleted: 0, filesChanged: 1,
    }),
  ];
  const sb = new Sidebar(list, newBtn, closedEl);
  sb.rerender = () =>
    sb.render(heroSessions, "h-1", [
      { id: "c-1", title: "kanban refactor", paneCount: 3, resumableCount: 2, closedAtMs: NOW - 35 * 60_000 },
    ], heroProjects, "projects");
  sb.rerender();
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

// ---- #hero workspace: two real panes with scripted terminal content -------
// Everything is the shipping UI code (header/footer builders, style.css,
// xterm with the app's theme); only the bytes written to the terminals are
// hand-scripted, since a still image needs no PTY behind it.
if (view === "hero") {
  const DIM = "\x1b[90m", RST = "\x1b[0m", GRN = "\x1b[32m",
        ORN = "\x1b[38;5;214m", BLD = "\x1b[1m", CYN = "\x1b[36m";

  const ccTranscript = [
    "",
    `${DIM}>${RST} make the holiday banner match the shop, and show me before and after`,
    "",
    `⏺ I'll capture the current banner first, then restyle it against the shop`,
    `  tokens.`,
    "",
    `${GRN}⏺${RST} ${BLD}Bash${RST}(node scripts/capture.mjs --route /?banner=holiday)`,
    `  ${DIM}⎿  captured design/banner-before.png (1280x400)${RST}`,
    "",
    `${GRN}⏺${RST} ${BLD}Read${RST}(src/styles/tokens.css)`,
    `  ${DIM}⎿  Read 41 lines${RST}`,
    "",
    `${GRN}⏺${RST} ${BLD}Update${RST}(src/banner.css)`,
    `  ${DIM}⎿  Updated src/banner.css with 12 additions and 7 removals${RST}`,
    "",
    `${GRN}⏺${RST} ${BLD}Bash${RST}(node scripts/capture.mjs --route /?banner=holiday)`,
    `  ${DIM}⎿  captured design/banner-after.png (1280x400)${RST}`,
    "",
    `⏺ The gradient is gone and the headline now uses the shop serif. The red`,
    `  stays on the call-to-action only. Before and after are in the journal.`,
    "",
    `${DIM}>${RST} love it. tighten the mobile crop a little`,
    "",
    `${ORN}✳${RST} Adjusting the mobile breakpoint… ${DIM}(1m 02s · esc to interrupt)${RST}`,
    "",
  ].join("\r\n");

  const viteTranscript = [
    "",
    `${DIM}PS C:\\dev\\storefront-web>${RST} npm run dev`,
    "",
    `  ${GRN}${BLD}VITE${RST} ${GRN}v6.0.3${RST}  ${DIM}ready in${RST} ${BLD}241${RST} ${DIM}ms${RST}`,
    "",
    `  ${GRN}➜${RST}  ${BLD}Local${RST}:   ${CYN}http://localhost:5173/${RST}`,
    `  ${GRN}➜${RST}  ${DIM}Network: use --host to expose${RST}`,
    "",
    `  ${DIM}14:32:07${RST} ${CYN}[vite]${RST} ${GRN}hmr update${RST} ${DIM}banner.css${RST}`,
    `  ${DIM}14:32:41${RST} ${CYN}[vite]${RST} ${GRN}hmr update${RST} ${DIM}banner.ts${RST}`,
    "",
  ].join("\r\n");

  interface HeroPaneSpec {
    paneId?: string;
    name: string; colorIndex: number;
    agentState: Leaf["agentState"]; activityDetail: string;
    branch: string; commitCount: number; ports: number[];
    agentType?: string; model?: string;
    active: boolean; flex: number; turnStartMs?: number;
  }

  function heroPane(spec: HeroPaneSpec, transcript: string): HTMLElement {
    const l = leaf({
      ...(spec.paneId ? { paneId: spec.paneId } : {}),
      name: spec.name, colorIndex: spec.colorIndex,
      agentState: spec.agentState, activityDetail: spec.activityDetail,
      branch: spec.branch, commitCount: spec.commitCount, ports: spec.ports,
      turnStartMs: spec.turnStartMs ?? 0,
    });
    const el = document.createElement("div");
    el.className = "pane" + (spec.active ? " pane--active" : "");
    el.dataset.color = String(l.colorIndex);
    el.dataset.state = l.agentState;
    el.style.flexGrow = String(spec.flex);

    const h = buildPaneHeader(l.paneId);
    h.nameEl.textContent = l.name;
    h.colorDotEl.dataset.color = String(l.colorIndex);
    h.stateDotEl.dataset.state = l.agentState;
    h.stateLabelEl.textContent =
      l.agentState === "idle" ? "" :
      l.agentState === "done" ? "idle" : l.agentState;
    applyChips(h.branchEl, h.commitsEl, l, spec.active);
    applyPorts(h.portsEl, l);
    applyAgentBadge(h.agentBadgeEl, spec.agentType);
    applyModelChip(h.modelEl, spec.agentType, spec.model);

    const termHost = document.createElement("div");
    termHost.className = "pane__term";

    const f = buildPaneFooter();
    applyPaneFooter(f, l, spec.active);

    el.append(h.root, termHost, f.root);

    const term = new Terminal({
      fontFamily:
        '"Geist Mono Variable", "Cascadia Code", "Cascadia Mono", Consolas, monospace',
      fontSize: 13,
      disableStdin: true,
      cursorBlink: false,
      theme: {
        background: "#1f1f1f",
        foreground: "rgba(255, 255, 255, 0.92)",
        cursor: "#76B9ED",
        cursorAccent: "#1f1f1f",
        selectionBackground: "rgba(118, 185, 237, 0.32)",
      },
    });
    const fitter = new FitAddon();
    term.loadAddon(fitter);
    term.open(termHost);
    setTimeout(() => { fitter.fit(); term.write(transcript); }, 80);
    return el;
  }

  // Layout: CC pane left; right column stacks the dev server over a pane
  // still booting under the mascot cover. (Orientation classes follow
  // workspace.ts: the class names the divider, so side-by-side is --v.)
  const stage = document.createElement("div");
  stage.className = "workspace__stage";
  document.getElementById("workspace")!.appendChild(stage);
  const split = document.createElement("div");
  split.className = "split split--v";
  stage.appendChild(split);

  split.appendChild(heroPane({
    paneId: "hero-cc",
    name: "holiday banner", colorIndex: 0,
    agentState: "working", activityDetail: "adjusting the mobile breakpoint",
    branch: "main", commitCount: 2, ports: [],
    agentType: "claude", model: "opus",
    active: true, flex: 1.7, turnStartMs: TWO_MIN_AGO,
  }, ccTranscript));

  const outerGutter = document.createElement("div");
  outerGutter.className = "split__gutter split__gutter--v";
  split.appendChild(outerGutter);

  const rightCol = document.createElement("div");
  rightCol.className = "split split--h";
  rightCol.style.flexGrow = "1";
  split.appendChild(rightCol);

  rightCol.appendChild(heroPane({
    name: "dev server", colorIndex: 2,
    agentState: "idle", activityDetail: "",
    branch: "main", commitCount: 0, ports: [5173],
    active: false, flex: 1,
  }, viteTranscript));

  const innerGutter = document.createElement("div");
  innerGutter.className = "split__gutter split__gutter--h";
  rightCol.appendChild(innerGutter);

  // Booting pane: empty terminal under the real "Setting up…" cover, so the
  // shot carries the mascot mid-performance.
  const bootPane = heroPane({
    name: "gift guide draft", colorIndex: 5,
    agentState: "idle", activityDetail: "",
    branch: "main", commitCount: 0, ports: [],
    agentType: "claude", model: "",
    active: false, flex: 1,
  }, "");
  // shuffleIdle off: the hero capture must land on the same frame every run.
  const cover = createSetupOverlay(false);
  bootPane.appendChild(cover.el);
  cover.show();
  rightCol.appendChild(bootPane);

  // Sidebar extras the shot should carry: the needs-you badge, the local
  // dev-server card, the cloud card, and an honest status line.
  const badge = document.getElementById("dash-badge")!;
  badge.textContent = "5";
  badge.style.display = "";
  document.getElementById("status-text")!.textContent = "7 sessions";
  const localArea = document.getElementById("local-area");
  if (localArea) {
    localArea.hidden = false;
    document.getElementById("local-card-title")!.textContent = "vite";
    document.getElementById("local-card-port")!.textContent = ":5173";
    document.getElementById("local-card-sub")!.textContent = "holiday banner";
  }
  const cloudArea = document.getElementById("cloud-area");
  if (cloudArea) {
    cloudArea.hidden = false;
    document.getElementById("cloud-card-title")!.textContent = "2 machines";
    document.getElementById("cloud-card-rate")!.textContent = "$0.41/hr";
    document.getElementById("cloud-card-sub")!.textContent = "e2-standard-4 · build-runner";
  }

  // ---- Inspector: fed through the hero.html host stub -------------------
  // The rail is the real inspector.ts following the CC pane; the stub answers
  // its inspector.request with a fixture journal (prompts, beats, tool calls,
  // two image rows) and serves the image bytes as inline SVG thumbnails.
  const isoAgo = (min: number, sec = 0) =>
    new Date(Date.now() - min * 60_000 - sec * 1000).toISOString();
  const iev = (kind: string, ts: string, over: Record<string, unknown> = {}) =>
    ({ kind, ts, text: "", verb: "", target: "", note: "", repeat: 1, ...over });

  const BANNER_BEFORE_SVG =
    '<svg xmlns="http://www.w3.org/2000/svg" width="640" height="200">' +
    '<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">' +
    '<stop offset="0" stop-color="#7b2ff7"/><stop offset="1" stop-color="#f107a3"/>' +
    '</linearGradient></defs>' +
    '<rect width="640" height="200" fill="url(#g)"/>' +
    '<text x="320" y="102" text-anchor="middle" font-family="Segoe UI, sans-serif" ' +
    'font-size="44" font-weight="800" fill="#ffffff">HOLIDAY SALE</text>' +
    '<text x="320" y="146" text-anchor="middle" font-family="Segoe UI, sans-serif" ' +
    'font-size="18" fill="#ffe9ff">UP TO 70% OFF EVERYTHING</text></svg>';
  const BANNER_AFTER_SVG =
    '<svg xmlns="http://www.w3.org/2000/svg" width="640" height="200">' +
    '<rect width="640" height="200" fill="#f6f1e8"/>' +
    '<rect x="1" y="1" width="638" height="198" fill="none" stroke="#e4dccd" stroke-width="2"/>' +
    '<text x="56" y="92" font-family="Georgia, serif" font-size="40" fill="#1e2a3a">The Holiday Shop</text>' +
    '<text x="58" y="128" font-family="Segoe UI, sans-serif" font-size="17" fill="#5d6570">' +
    'Considered gifts, wrapped and shipped by the 22nd</text>' +
    '<rect x="56" y="148" width="118" height="34" rx="17" fill="#b4372f"/>' +
    '<text x="115" y="170" text-anchor="middle" font-family="Segoe UI, sans-serif" ' +
    'font-size="15" font-weight="600" fill="#ffffff">Shop gifts</text></svg>';

  const inspectorData = {
    type: "inspector.data",
    paneId: "hero-cc",
    hasAgent: true,
    events: [
      iev("prompt", isoAgo(9), { text: "make the holiday banner match the rest of the shop, and show me before and after" }),
      iev("beat", isoAgo(8, 55), { text: "I'll capture the current banner first, then restyle it against the shop tokens." }),
      iev("work", isoAgo(8, 30), { verb: "Bash", target: "scripts/capture.mjs", note: "banner-before.png" }),
      iev("image", isoAgo(8, 20), { verb: "shared", target: "img-before" }),
      iev("work", isoAgo(8), { verb: "Read", target: "src/styles/tokens.css", note: "41 lines" }),
      iev("work", isoAgo(7), { verb: "Update", target: "src/banner.css", note: "+12 −7", repeat: 2 }),
      iev("work", isoAgo(6), { verb: "Update", target: "src/banner.ts", note: "+6 −2" }),
      iev("work", isoAgo(5, 30), { verb: "Bash", target: "scripts/capture.mjs", note: "banner-after.png" }),
      iev("image", isoAgo(5, 20), { verb: "shared", target: "img-after" }),
      iev("beat", isoAgo(5), { text: "The gradient is gone and the headline now uses the shop serif. The red stays on the call-to-action only." }),
      iev("prompt", isoAgo(2), { text: "love it. tighten the mobile crop a little" }),
      iev("work", isoAgo(1), { verb: "Update", target: "src/banner.css", note: "+3 −1" }),
    ],
    vitals: {
      model: "opus", inputTokens: 48210, outputTokens: 9834,
      cacheReadTokens: 512000, cacheWriteTokens: 88000, costUsd: 1.87,
      contextTokens: 61400, contextMax: 200000,
    },
    files: [
      { path: "src/banner.css", added: 38, deleted: 21 },
      { path: "src/banner.ts", added: 6, deleted: 2 },
      { path: "index.html", added: 2, deleted: 0 },
    ],
    added: 46,
    deleted: 23,
  };

  (window as any).__heroRoute = (msg: { type: string; imageId?: string; paneId?: string; variant?: string }) => {
    if (msg.type === "inspector.request") return [inspectorData];
    if (msg.type === "inspector.image") {
      const svg = msg.imageId === "img-before" ? BANNER_BEFORE_SVG : BANNER_AFTER_SVG;
      return [{
        type: "inspector.image.data", paneId: msg.paneId, imageId: msg.imageId,
        variant: msg.variant, mediaType: "image/svg+xml", data: btoa(svg),
      }];
    }
    return [];
  };

  initInspector();
  // Minimal state push so the rail follows the CC pane (name, tag, live poll).
  (window as any).__heroPush?.({
    type: "state",
    activeSessionId: "h-1",
    activePaneId: "hero-cc",
    homeDir: "C:\\Users\\demo",
    sessions: [projectTab({
      id: "h-1", title: "holiday banner", projectId: "h-shop",
      agentState: "working", activityDetail: "adjusting the mobile breakpoint",
      paneCount: 3, workingCount: 1, ports: [5173],
      turnStartMs: TWO_MIN_AGO, doneAtMs: 0,
      rootPane: {
        kind: "split", id: "hero-split", orientation: "v",
        children: [
          leaf({ paneId: "hero-cc", name: "holiday banner", agentState: "working", colorIndex: 0, branch: "main" }),
          leaf({ paneId: "hero-dev", name: "dev server", agentState: "idle", colorIndex: 2, branch: "main", ports: [5173] }),
          leaf({ paneId: "hero-boot", name: "gift guide draft", agentState: "idle", colorIndex: 5, branch: "main" }),
        ],
      },
    })],
    projects: [],
    closedSessions: [],
    prefs: { inspectorOpen: true },
  });
}

// #modelmenu — the per-pane Claude model picker: mock pane headers wearing the
// quiet model chip, plus the flyout in both states (no usage data — the normal
// case — and Fable at its weekly limit, disabled with a reset hint). The left
// menu is a DOM clone of the real flyout (the module allows one live menu at a
// time); the right one is live, so opening this page in a browser is clickable.
// #botfaces — the team room's avatars (bot-face.ts). The six hats down the
// rows and the four states across; then every eyewear and every extra on a
// beanie (idle + working, so the tools act); the six temperaments; and a
// colour-mode toggle. ?t=<ms> pins every face at that loop time (freezeFaces)
// so a capture is a frame by number; ?color=1 opens in colour mode. The 96 px
// cell is for review, the 28 px twin beside it is the real size in the room.
if (view === "botfaces") {
  document.getElementById("app")!.classList.add("app--inspector-collapsed");   // the grid wants the width
  const stage = document.getElementById("workspace")!;
  // Opaque stage on purpose: the workspace is transparent for Mica, and a
  // headless capture drops everything drawn over that region (CLAUDE.md, "both
  // capture methods lie"). The room's surface is what the faces sit on anyway.
  stage.style.cssText = "display:block;overflow:auto;padding:24px 32px 48px;border-radius:var(--r-card);background:var(--color-terminal-bg);color:var(--color-text-primary);font:13px/1.4 var(--font-text)";
  const css = document.createElement("style");
  css.textContent = `
    .bfh h2{font-size:16px;font-weight:600;line-height:1.2;margin:32px 0 12px}
    .bfh h2:first-of-type{margin-top:0}
    .bfh .grid{display:grid;gap:8px 12px;align-items:center}
    .bfh .grid .hd{font-size:12px;color:var(--color-text-tertiary);padding-bottom:4px}
    .bfh .grid .rl{font-size:14px;font-weight:500}
    .bfh .grid .rl small{display:block;font-size:12px;font-weight:400;color:var(--color-text-tertiary)}
    .bfh .cell{display:flex;align-items:flex-end;gap:16px;padding:12px;border-radius:8px;background:var(--color-layer);border:1px solid var(--color-stroke)}
    .bfh .av{flex:none;display:block}
    .bfh .cell .av--28{margin-bottom:8px}
    .bfh .ctl{display:flex;align-items:center;gap:12px;margin:0 0 16px;font-size:12px;color:var(--color-text-tertiary)}
    .bfh .ctl button{font:inherit;color:var(--color-text-primary);background:var(--color-subtle-tertiary);border:1px solid var(--color-stroke);border-radius:4px;padding:4px 12px;cursor:pointer}`;
  stage.append(css);
  const root = document.createElement("div");
  root.className = "bfh";
  stage.append(root);

  // The positions the hats mean, with the temperament and tag each wore in
  // its mockup (character variant's cast; hats variant's palette order).
  const CAST: { hat: FaceHat; pos: string; temper: FaceTemper; tag: number }[] = [
    { hat: "captain", pos: "Team lead", temper: "lead", tag: 0 },
    { hat: "beanie", pos: "Frontend dev", temper: "quick", tag: 1 },
    { hat: "hardhat", pos: "Backend dev", temper: "steady", tag: 3 },
    { hat: "beret", pos: "Designer", temper: "curious", tag: 5 },
    { hat: "deerstalker", pos: "QA", temper: "wary", tag: 2 },
    { hat: "tophat", pos: "Senior analyst", temper: "keen", tag: 4 },
  ];
  const HAT_NAME: Record<FaceHat, string> = { captain: "captain's cap", beanie: "beanie", hardhat: "hard hat", beret: "beret", deerstalker: "deerstalker", tophat: "top hat" };

  const cell = (look: BotLook, state: FaceState, tag: number): HTMLElement => {
    const c = document.createElement("div");
    c.className = "cell";
    for (const sz of [96, 28]) {
      const box = document.createElement("div");
      box.className = `av av--${sz}`;
      box.style.cssText = `width:${sz}px;height:${sz}px;--tag:var(--color-pane-tag-${tag})`;
      box.append(createBotFace(look, tag, state, 0).el);
      c.append(box);
    }
    return c;
  };
  const grid = (cols: string[], colWidth: string): HTMLElement => {
    const g = document.createElement("div");
    g.className = "grid";
    g.style.gridTemplateColumns = `168px repeat(${cols.length}, ${colWidth})`;
    g.insertAdjacentHTML("beforeend", `<div class="hd"></div>${cols.map((s) => `<div class="hd">${s}</div>`).join("")}`);
    return g;
  };
  const rowLabel = (g: HTMLElement, title: string, sub: string) =>
    g.insertAdjacentHTML("beforeend", `<div class="rl">${title}<small>${sub}</small></div>`);

  // controls
  const ctl = document.createElement("div");
  ctl.className = "ctl";
  const toggle = document.createElement("button");
  toggle.type = "button";
  const label = () => { toggle.textContent = faceColorMode() ? "Colour: on" : "Colour: off"; };
  toggle.onclick = () => { setFaceColorMode(!faceColorMode()); label(); };
  ctl.append(toggle);
  const note = document.createElement("span");
  note.textContent = "?t=<ms> pins a frame; ?color=1 opens in colour";
  ctl.append(note);
  root.append(ctl);

  // 1. the hats × the states
  root.insertAdjacentHTML("beforeend", `<h2>Hats (the position) × states</h2>`);
  const g1 = grid([...FACE_STATES], "1fr");
  for (const m of CAST) {
    rowLabel(g1, m.pos, `${HAT_NAME[m.hat]} · ${m.temper}`);
    for (const s of FACE_STATES) g1.append(cell({ hat: m.hat, eyewear: "monocle", extra: "none", temper: m.temper }, s, m.tag));
  }
  root.append(g1);

  // 2. every eyewear, every extra — on a beanie, idle and working
  root.insertAdjacentHTML("beforeend", `<h2>Eyewear, on a beanie</h2>`);
  const g2 = grid(["idle", "working", "waiting"], "1fr");
  FACE_EYEWEAR.forEach((e, i) => {
    rowLabel(g2, e, "steady · none");
    for (const s of ["idle", "working", "waiting"] as const) g2.append(cell({ hat: "beanie", eyewear: e, extra: "none", temper: "steady" }, s, i % 6));
  });
  root.append(g2);
  root.insertAdjacentHTML("beforeend", `<h2>Extras, on a beanie</h2>`);
  const g3 = grid(["idle", "working", "asleep"], "1fr");
  FACE_EXTRAS.forEach((x, i) => {
    rowLabel(g3, x, "steady · monocle");
    for (const s of ["idle", "working", "asleep"] as const) g3.append(cell({ hat: "beanie", eyewear: "monocle", extra: x, temper: "steady" }, s, i % 6));
  });
  root.append(g3);

  // 3. the temperaments — same bird, six different people
  root.insertAdjacentHTML("beforeend", `<h2>Temperaments, on a beanie</h2>`);
  const g4 = grid([...FACE_STATES], "1fr");
  FACE_TEMPERS.forEach((t, i) => {
    rowLabel(g4, t, "monocle · none");
    for (const s of FACE_STATES) g4.append(cell({ hat: "beanie", eyewear: "monocle", extra: "none", temper: t }, s, i % 6));
  });
  root.append(g4);
  void FACE_HATS;

  // ?only=hats|eyewear|extras|tempers keeps one block, so a capture fits a window
  const only = viewParams.get("only");
  if (only) {
    const keep: Record<string, HTMLElement> = { hats: g1, eyewear: g2, extras: g3, tempers: g4 };
    for (const [k, g] of Object.entries(keep)) {
      if (k === only) continue;
      g.previousElementSibling?.remove();   // its heading
      g.remove();
    }
  }

  if (viewParams.get("color") === "1") setFaceColorMode(true);
  label();
  if (viewParams.has("t")) freezeFaces(+viewParams.get("t")! || 0);
}

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
  const active = mk("active pane — actions revealed", "storefront-api", true, "claude");
  mk("inactive pane — actions hidden until hover", "usage report", false, "claude");
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
    name: "storefront-web",
    path: "C:\\dev\\storefront-web",
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
    startedByPerch: true,
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
        startedByPerch: true,
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
        startedByPerch: true,
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
        startedByPerch: true,
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
        startedByPerch: true,
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
        agentName: "nightly-report",
        task: "Run the nightly report for the shopping app vs the July build",
        paneId: "5b7d",
        isOrphan: false,
        agentState: "done",
      },
      {
        // GPU radar: a Terraform-provisioned A100 box nobody here started. View-
        // only — no agent, no kill, but costed so it can't hide.
        id: "us-central1-c/tf-runner",
        startedByPerch: false,
        name: "tf-runner-us-central1-c-l0jl",
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


// ---- Team room ---------------------------------------------------------------
// #team          — the room over a 3-bot team: Ada working (2m), Bo blocked on a
//                  permission prompt, Cy asleep. ~20 ledger rows covering every
//                  kind, two work folds, a peer message, a pending post, and the
//                  "older messages aren't shown" banner.
// #team-empty    — a project with a team folder but no bots yet.
// #team-sidebar  — the sidebar alone: the team row (with an unread count) and
//                  the Bots drawer open (a bot's tab is active), its rows
//                  wearing their position tags.
// #team-sidebar-shut — the same with an ordinary tab active: the drawer as
//                  it starts, shut, wearing the bots' most urgent state.
// #newbot*       — the dialog: fresh / mid-generation / reviewing the brief /
//                  the failure recovery / the existing-position path.
if (view === "team" || view === "team-activity" || view === "team-empty" || view.startsWith("team-sidebar") || view.startsWith("newbot")) {
  const teamProjects: ProjectView[] = [
    {
      id: "p-ptp", name: "storefront-web", path: "C:\\dev\\storefront-web",
      team: {
        positions: [
          { slug: "frontend-dev", name: "Frontend dev", purpose: "Owns everything under src/web: the sidebar, the panes, the dialogs and the CSS tokens.", model: "sonnet", hasBrief: true },
          { slug: "backend-dev", name: "Backend dev", purpose: "Owns the WPF host, the IPC pipes, the hook handler and the CLI.", model: "", hasBrief: true },
          { slug: "designer", name: "Designer", purpose: "Keeps every surface on the constitution; mocks new surfaces before code.", model: "", hasBrief: false },
        ],
        bots: [
          { botId: "b-ada", nickname: "Ada", positionSlug: "frontend-dev", positionName: "Frontend dev", sessionId: "s-ada", peerName: "ada",
            look: { hat: "beanie", eyewear: "monocle", extra: "scarf", temper: "quick" } },
          { botId: "b-bo", nickname: "Bo", positionSlug: "backend-dev", positionName: "Backend dev", sessionId: "s-bo", peerName: "bo",
            look: { hat: "hardhat", eyewear: "rect", extra: "spanner", temper: "steady" } },
          { botId: "b-cy", nickname: "Cy", positionSlug: "designer", positionName: "Designer", sessionId: "s-cy", peerName: "cy-2",
            look: { hat: "beret", eyewear: "round", extra: "pencil", temper: "curious" } },
        ],
        lead: "b-ada",
        // The task column: two cards — one the lead has asked to confirm, one
        // still moving.
        tasks: [
          {
            id: "t2", title: "Loading states for KPI Performance: skeleton cards and charts, buttons disabled while data loads",
            status: "open", setBy: "Ada", createdAtMs: Date.now() - 35 * 60_000,
            items: [
              { botId: "b-ada", bot: "Ada", title: "Gate: review the diff, then merge and push", status: "todo", note: "", updatedAtMs: Date.now() - 1800_000 },
              { botId: "b-bo", bot: "Bo", title: "Template + harness for the loading states", status: "doing", note: "template patched, shooting now", updatedAtMs: Date.now() - 240_000 },
            ],
            wrapping: [],
          },
          {
            id: "t1", title: "Ship the team room: faces in the roster, bots folded in the sidebar, no dropped posts",
            status: "review", setBy: "Ada", reviewBy: "Ada", createdAtMs: Date.now() - 2 * 3600_000,
            items: [
              { botId: "b-ada", bot: "Ada", title: "Faces in the room and roster", status: "done", note: "harness shot is in", updatedAtMs: Date.now() - 600_000 },
              { botId: "b-bo", bot: "Bo", title: "Ledger fan-out and the submit check", status: "done", note: "", updatedAtMs: Date.now() - 1200_000 },
              { botId: "b-cy", bot: "Cy", title: "Empty-state mock", status: "blocked", note: "waiting on the copy", updatedAtMs: Date.now() - 300_000 },
            ],
            wrapping: [],
          },
        ],
      },
    },
    { id: "p-gm", name: "home-tools", path: "C:\\dev\\home-tools", team: { positions: [], bots: [] } },
  ];
  const permAsk = { text: "Allow running `npm install` in the worktree?", level: "error" as const };
  const teamSessions: SessionView[] = [
    projectTab({
      id: "s-ada", title: "Ada",
      rootPane: leaf({ name: "Ada", agentState: "working", activityDetail: "editing sidebar.ts", branch: "team/ada", colorIndex: 0, ahead: 2, aheadMine: 2 }),
      agentState: "working", activityDetail: "editing sidebar.ts", workingCount: 1, turnStartMs: TWO_MIN_AGO,
      worktreeBranch: "team/ada", branch: "team/ada", linesAdded: 143, linesDeleted: 31, filesChanged: 4, ahead: 2, aheadMine: 2,
    }),
    projectTab({
      id: "s-bo", title: "Bo",
      rootPane: leaf({ name: "Bo", agentState: "permission", branch: "team/bo", colorIndex: 3, notification: permAsk }),
      agentState: "permission", notification: permAsk, waitingCount: 1,
      worktreeBranch: "team/bo", branch: "team/bo", linesAdded: 12, linesDeleted: 2, filesChanged: 1,
    }),
    projectTab({
      id: "s-cy", title: "Cy",
      rootPane: leaf({ name: "Cy", agentState: "done", branch: "team/cy", colorIndex: 5 }),
      agentState: "done", dormant: true, doneAtMs: Date.now() - 41 * 60_000,
      worktreeBranch: "team/cy", branch: "team/cy",
    }),
    projectTab({
      id: "s-plain", title: "signup flow",
      rootPane: leaf({ name: "signup flow", agentState: "done", branch: "main", colorIndex: 1 }),
      doneAtMs: Date.now() - 47_000, linesAdded: 40, linesDeleted: 9, filesChanged: 2,
    }),
  ];
  const teamState: StateMessage = {
    type: "state",
    activeSessionId: "s-ada",
    activePaneId: (teamSessions[0].rootPane as Extract<PaneTreeView, { kind: "leaf" }>).paneId,
    homeDir: "C:\\Users\\me",
    sessions: teamSessions,
    projects: teamProjects,
    closedSessions: [],
    prefs: { fontSize: 13, sidebarMode: "projects", inspectorOpen: true },
  };

  // The ledger. Times run backwards from now so continuation grouping (3 min)
  // and the hover stamps read plausibly.
  const at = (minsAgo: number, secs = 0) => new Date(Date.now() - minsAgo * 60_000 - secs * 1000).toISOString();
  let seq = 40;   // truncated: the room starts mid-history
  const row = (over: Partial<TeamEntryView> & { kind: TeamEntryView["kind"]; from: string; text: string; ts: string }): TeamEntryView =>
    ({ seq: ++seq, ...over });
  const work = (from: string, botId: string, ts: string, verb: string, target: string, repeat?: number) =>
    row({ kind: "work", from, botId, ts, verb, target, text: "", ...(repeat ? { repeat } : {}) });
  const entries: TeamEntryView[] = [
    row({ kind: "system", from: "perch", ts: at(58), text: "Ada joined as Frontend dev", event: "joined" }),
    row({ kind: "system", from: "perch", ts: at(57), text: "Bo joined as Backend dev", event: "joined" }),
    row({ kind: "user", from: "you", ts: at(55), to: "everyone", text: "@everyone introduce yourselves in one line, then Ada: the sidebar's team row is misaligned when a project is collapsed. Bo: is `team.data` pushed while the room is open?" }),
    row({ kind: "beat", from: "Ada", botId: "b-ada", ts: at(54, 40), text: "Ada, Frontend dev — I own **src/web**: the sidebar, panes, dialogs and tokens. I'll take the team row alignment now." }),
    row({ kind: "beat", from: "Bo", botId: "b-bo", ts: at(54, 20), text: "Bo, Backend dev — host, IPC and the hook handler. Yes: while the room is open the host pushes `team.data` on every new ledger entry; the page's poll is only a fallback." }),
    work("Ada", "b-ada", at(52), "Read", "sidebar.ts"),
    work("Ada", "b-ada", at(51, 40), "Read", "style.css"),
    work("Ada", "b-ada", at(51, 10), "Grep", "team-row"),
    work("Ada", "b-ada", at(50), "Edit", "sidebar.ts"),
    work("Ada", "b-ada", at(49, 30), "Edit", "style.css"),
    work("Ada", "b-ada", at(49), "Bash", "npm test", 2),
    row({ kind: "beat", from: "Ada", botId: "b-ada", ts: at(48), text: "Found it — the row sat inside the folded list, so it collapsed with the tabs. Moved it to a sibling of the header; it now stays visible when the group is shut, and the unread count can't fold away." }),
    row({ kind: "peer", from: "Ada", botId: "b-ada", ts: at(47), to: ["Bo"], text: "Bo — I need `unread` in the team row. Can `team.data` carry `lastSeq` even when `entries` is empty? Then the page can count without a second request." }),
    row({ kind: "beat", from: "Bo", botId: "b-bo", ts: at(46), text: "It already does: `lastSeq` is the ledger head on every reply, entries or not. Nothing to change on my side." }),
    work("Bo", "b-bo", at(45), "Read", "TeamController.cs"),
    work("Bo", "b-bo", at(44, 30), "Edit", "TeamController.cs"),
    work("Bo", "b-bo", at(44), "Bash", "dotnet test src/Perch.Tests"),
    row({ kind: "note", from: "Bo", botId: "b-bo", ts: at(43), text: "Pushed the ledger fan-out to `team.data` — the room now updates the instant a bot posts. Ada's page work can rely on it." }),
    row({ kind: "system", from: "perch", ts: at(40), text: "Cy joined as Designer", event: "joined" }),
    row({ kind: "user", from: "you", ts: at(38), to: ["Cy"], text: "@Cy before Ada polishes the room, mock the empty state — what does a project with no bots see?" }),
    row({ kind: "beat", from: "Cy", botId: "b-cy", ts: at(36), text: "Mocked two options in `design-loop/team-empty-options.html`: a centered card with a single accent CTA, and a quieter inline line under the header. Recommending the card — it's the one place the constitution allows centering, and a first-run state should be unmissable." }),
    row({ kind: "system", from: "perch", ts: at(30), text: "Cy is asleep", event: "asleep" }),
    row({ kind: "user", from: "you", ts: at(6), to: ["Ada"], text: "Ada — ship the row fix and post when the harness shot is in." }),
    row({ kind: "beat", from: "Ada", botId: "b-ada", ts: at(5, 30), text: "On it. Running the harness now; shot lands in `design-loop/team-sidebar.png`." }),
    row({ kind: "beat", from: "Ada", botId: "b-ada", ts: at(4), text: "Typecheck and tests are green. Taking the screenshot." }),
    // A hand-off, labelled: what the message IS, not just who it's for.
    row({ kind: "peer", from: "Bo", botId: "b-bo", ts: at(3, 40), to: ["Ada"], note: "handoff", text: "Template and harness for the loading states are on branch bo (dad7d54e). Review the diff and merge when you're happy; I stopped the local server on 5103." }),
    row({ kind: "peer", from: "Ada", botId: "b-ada", ts: at(3, 20), to: ["Bo"], note: "question", text: "Does the skeleton fall back to the plain spinner when the chart lib isn't loaded yet?" }),
    // A screenshot shared to the room (the path becomes a thumbnail).
    row({ kind: "note", from: "Bo", botId: "b-bo", ts: at(3), text: "Loading state, dark theme — the skeleton cards and the disabled buttons. Full set in the harness at http://localhost:5103/harness#kpi-loading", image: "C:\\dev\\storefront-web\\design-loop\\kpi-loading-bdm-dark.png" }),
    // A permission card: Bo wants to run something auto mode won't approve.
    row({ kind: "system", from: "perch", ts: at(2, 30), to: ["Bo"], note: "perm-7a1c", event: "permission",
      text: "Bo wants to run Bash: git push origin bo",
      summary: JSON.stringify({ command: "git push origin bo", description: "Push the loading-states branch", timeout: 120000 }) }),
    // A question card with choices.
    row({ kind: "system", from: "Cy", botId: "b-cy", ts: at(2), to: ["Cy"], note: "ask-3f9e", event: "ask",
      text: "Which empty state should I build?", choices: ["Centered card", "Inline line"] }),
    // A trust card and a review card, so every card kind's frame is in one shot.
    row({ kind: "system", from: "perch", ts: at(2, 20), to: ["Cy"], event: "trust",
      text: "Cy asks: trust its new folder?" }),
    row({ kind: "system", from: "perch", ts: at(2, 10), taskId: "t-2", event: "task.review",
      text: "Ada says \"Sidebar team row\" is done — confirm on its card" }),
    // A bot's long piece of work: a card here, the document in the panel.
    row({ kind: "artefact", from: "Bo", botId: "b-bo", ts: at(2, 5), target: "a1b2c3d4", note: "md",
      text: "Draft ticket: bid-shading prepared table",
      summary: "Scope, columns, acceptance — for Galina, not created yet" }),
    // A message with real markdown in it: a table and a list, formatted.
    row({ kind: "beat", from: "Bo", botId: "b-bo", ts: at(2, 2), text:
      "Counter coverage as measured this morning:\n\n"
      + "| Counter | Populated | Source |\n|---|---:|---|\n"
      + "| `avgUserClearPrice` | 66% | bid_event |\n| `pbundle_loss_fixed` | 91% | loss_event |\n\n"
      + "Two things follow:\n\n- the 66% column can't be a hard acceptance rule\n"
      + "- `loss_event` is the only source that covers every SSP\n" }),
    // Auto mode's classifier refusing a command: information only, but it has
    // to be VISIBLE — this is what Joseph could not find in the room.
    row({ kind: "system", from: "Bo", botId: "b-bo", ts: at(1, 55), event: "denied",
      text: "Bo: auto mode blocked Bash: nohup python services/pricing-agent-monitor/app.py --port 5108 > C:/tmp/pam-local.log 2>&1 & — Blocked by classifier" }),
    row({ kind: "system", from: "perch", ts: at(1, 50), text: "Copied to Ada for the board", event: "cc" }),
    row({ kind: "user", from: "you", ts: at(1, 30), to: ["Bo"], text: "@Bo hold the push until Ada's review is in; I'll allow it then." }),
    // Reactions: yours on Bo's hand-off (highlighted), Ada's on your post.
    row({ kind: "reaction", from: "you", ts: at(1, 20), text: "👀", note: String(seq - 6) }),
    row({ kind: "reaction", from: "Ada", botId: "b-ada", ts: at(1, 10), text: "✅", note: String(seq - 7) }),
    row({ kind: "reaction", from: "Ada", botId: "b-ada", ts: at(1), text: "👋", note: String(seq - 2) }),
  ];
  const fixture: TeamDataMessage = { type: "team.data", projectId: "p-ptp", entries, lastSeq: seq, truncated: true };
  (window as unknown as { __teamFixture: TeamDataMessage }).__teamFixture = fixture;
  // Nothing for home-tools yet: an empty, non-truncated ledger.
  const emptyFixture: TeamDataMessage = { type: "team.data", projectId: "p-gm", entries: [], lastSeq: 0 };

  // The shot shows the room the way it opens: tool activity off (the header's
  // toggle brings it back).
  try { localStorage.setItem("perch.team.activity", view === "team-activity" ? "1" : "0"); } catch { /* file:// */ }

  // #team-sidebar wants an unread count: pretend you last looked three rows ago.
  if (view.startsWith("team-sidebar")) {
    try { localStorage.setItem("perch.team.seen", JSON.stringify({ "p-ptp": seq - 3 })); } catch { /* file:// */ }
    feedTeamFixture(fixture);
  }

  // main.ts isn't loaded here, so route the dialog's host replies ourselves.
  onHostMessage((m) => {
    if (m.type === "team.brief.progress") applyBriefProgress(m);
    else if (m.type === "team.brief.result") applyBriefResult(m);
    else if (m.type === "team.reference.picked") applyReferencePicked(m);
  });

  const sb = new Sidebar(list, newBtn, closedEl);
  const activeTab = view === "team-sidebar-shut" ? "s-plain" : "s-ada";
  sb.rerender = () => sb.render(teamSessions, activeTab, [], teamProjects, "projects");
  sb.rerender();
  onTeamRoomChange(() => sb.rerender?.());
  applyTeamState(teamState);

  if (view === "team" || view === "team-activity") {
    feedTeamFixture(fixture);
    openTeamRoom("p-ptp");
    // No host here, so answer the panel's own requests: the recent list, then
    // the document it opens.
    applyArtefactIndex({
      type: "team.artefact.index", projectId: "p-ptp",
      items: [
        { id: "a1b2c3d4", title: "Draft ticket: bid-shading prepared table", kind: "md", from: "Bo", tsMs: Date.now() - 125_000,
          summary: "Scope, columns, acceptance — for Galina, not created yet" },
        { id: "b2c3d4e5", title: "Counter validation plan", kind: "md", from: "Ada", tsMs: Date.now() - 3_600_000 },
        { id: "c3d4e5f6", title: "Loss codes by SSP", kind: "csv", from: "Bo", tsMs: Date.now() - 7_200_000 },
      ],
    });
    applyArtefact({
      type: "team.artefact.data", projectId: "p-ptp", id: "a1b2c3d4", kind: "md", from: "Bo", tsMs: Date.now() - 125_000,
      title: "Draft ticket: bid-shading prepared table",
      content:
        "# Bid shading: bid-level prepared table\n\n"
        + "One row per outgoing bid, built daily on the existing ingestion. A condensed copy of the\n"
        + "loss table with win and impression outcomes joined from `bid_event`.\n\n"
        + "## Label\n\n"
        + "`lossprice` where `losscode = 102` (lost to a higher bid). Other codes are not auction\n"
        + "losses: excluded from the label, kept as `lossCode`.\n\n"
        + "| Column | Populated | Note |\n|---|---:|---|\n"
        + "| `lossminprice` | 2.5% | a carried column, never a label |\n"
        + "| `bidPricePreShading` | 0% | null on the NN path until PK-7225 ships |\n\n"
        + "## Acceptance\n\n"
        + "- partitioned by day; bids per day match `bid_event`\n"
        + "- `lossprice` coverage per SSP reported daily\n"
        + "- shaded / shadable populated and consistent across all three tables\n\n"
        + "> Open for Joseph: table name and retention, and whether \"condensed\" means all bids or\n"
        + "> only shadable campaigns.\n",
    });
  } else if (view === "team-empty") {
    feedTeamFixture(emptyFixture);
    openTeamRoom("p-gm");
  } else if (view.startsWith("newbot")) {
    // A project with no positions yet for the plain dialog; the one with
    // positions for the "existing position" path.
    const fresh: ProjectView = { id: "p-gm", name: "home-tools", path: "C:\\dev\\home-tools", team: { positions: [], bots: [] } };
    if (view === "newbot-reuse") {
      showNewBotDialog(teamProjects[0], { positionSlug: "frontend-dev" });
    } else {
      showNewBotDialog(fresh);
      if (view !== "newbot") {
        // Fill the fields the way a person would, then press Generate; the
        // harness.html stub answers with progress ticks and a result (or an
        // error for #newbot-error, or nothing after progress for #newbot-generating).
        setTimeout(() => {
          const set = (sel: string, v: string) => {
            const e = document.querySelector<HTMLInputElement | HTMLTextAreaElement>(sel);
            if (!e) return;
            e.value = v;
            e.dispatchEvent(new Event("input", { bubbles: true }));
          };
          const inputs = Array.from(document.querySelectorAll<HTMLInputElement>(".newbot-card input.newtab-input"));
          if (inputs[0]) { inputs[0].value = "Ada"; inputs[0].dispatchEvent(new Event("input", { bubbles: true })); }
          if (inputs[1]) { inputs[1].value = "Frontend dev"; inputs[1].dispatchEvent(new Event("input", { bubbles: true })); }
          set(".newbot-card textarea.newbot-area:not(.newbot-brief)",
            "Owns everything under src/web — the sidebar, the panes, the dialogs and the CSS tokens. Keeps the chrome calm and on the constitution.");
          const buttons = Array.from(document.querySelectorAll<HTMLButtonElement>(".newbot-card .projects-card__btn"));
          buttons.find((b) => b.textContent === "Generate brief")?.click();
        }, 80);
      }
    }
  }
}
