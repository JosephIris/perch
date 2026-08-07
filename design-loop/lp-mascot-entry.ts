// Mounts the REAL app into the LP design mockups (lp-design-b.html):
// the mascot rig, the live workspace demo, and the actual Sidebar — the same
// imports the shipped site uses (site/src/main.ts). Bundled to lp-mascot.js
// by lp-mascot-build.mjs; lp-app.css carries the app stylesheet. Mounts:
//   #rig-hero        — the hero wire (click to replay)
//   #rig-cta         — a second wire scene above the closing CTA
//   #rig-apple       — the apple slapstick loop, self-contained
//   #demo-workspace  — the full live workspace demo (site/src/workspace-demo.ts)
//   #demo-sidebar    — the app's real Sidebar in projects mode, active row
//                      peeked so the moon + label fades render statically
import {
  pose, buildScene, shuffledOrder, REST_T,
  type BeatOrder,
} from "../src/web/src/setup-overlay.js";
import { buildAppleScene } from "../site/src/apple-scene.js";
import { Sidebar } from "../src/web/src/sidebar.js";
import { startSpinnerTicker } from "../src/web/src/spinner.js";
import { startElapsedTicker } from "../src/web/src/elapsed.js";
import { buildWorkspaceDemo } from "../site/src/workspace-demo.js";

const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;

/* ---- fixtures: the storefront-web scenario (site/src/main.ts) ---------- */
const T0 = Date.now();
function fxLeaf(id: string, o: any = {}): any {
  return {
    kind: "leaf", paneId: id + "-p", name: o.title ?? "", colorIndex: 0,
    agentState: o.agentState ?? "idle", activityDetail: o.activityDetail ?? "",
    branch: o.branch ?? "main", ports: o.ports ?? [], notification: null,
    commitCount: 0, linesAdded: o.linesAdded ?? 0, linesDeleted: o.linesDeleted ?? 0,
    filesChanged: 0, ahead: o.ahead ?? 0, aheadMine: o.aheadMine ?? 0,
    turnStartMs: o.turnStartMs ?? 0,
  };
}
function fxSession(o: any): any {
  return {
    shell: "pwsh", worktreeBranch: "", projectId: o.projectId ?? "",
    branch: "main", ports: [], notification: null, paneCount: 1,
    waitingCount: 0, workingCount: o.agentState === "working" ? 1 : 0,
    linesAdded: 0, linesDeleted: 0, filesChanged: 0, ahead: 0, aheadMine: 0,
    turnStartMs: 0, doneAtMs: 0, activityDetail: "",
    ...o, rootPane: fxLeaf(o.id, o),
  };
}
const DEMO_SESSIONS: any[] = [
  fxSession({ id: "holiday-banner", title: "holiday banner", projectId: "sw",
    agentState: "working", activityDetail: "adjusting the mobile breakpoint",
    turnStartMs: T0 - 125_000, ahead: 3, aheadMine: 2 }),
  fxSession({ id: "fix-checkout", title: "fix checkout css", projectId: "sw",
    agentState: "done", doneAtMs: T0 - 49_000, linesAdded: 12, linesDeleted: 7,
    ahead: 3, aheadMine: 1 }),
  fxSession({ id: "seo-audit", title: "seo audit pass", projectId: "sw",
    agentState: "done", doneAtMs: T0 - 240_000 }),
  fxSession({ id: "cleanup", title: "cleanup old backups", projectId: "sw",
    agentState: "working", activityDetail: "removing stale archives",
    turnStartMs: T0 - 130_000 }),
  fxSession({ id: "invoice", title: "invoice export", projectId: "sw",
    agentState: "done", doneAtMs: T0 - 1_320_000 }),
  // Two slept tabs — the project's Idle (2) drawer.
  fxSession({ id: "gift-guide", title: "gift guide draft", projectId: "sw",
    dormant: true }),
  fxSession({ id: "press-kit", title: "press kit copy", projectId: "sw",
    dormant: true }),
  fxSession({ id: "photo-sync", title: "photo backup sync", projectId: "ht",
    agentState: "done", doneAtMs: T0 - 7_200_000 }),
  fxSession({ id: "recipe", title: "recipe importer", projectId: "ht",
    agentState: "done", doneAtMs: T0 - 18_000_000 }),
];
const DEMO_PROJECTS: any[] = [
  { id: "sw", name: "storefront-web", path: "C:\\dev\\storefront-web" },
  { id: "ht", name: "home-tools", path: "C:\\dev\\home-tools" },
];

/* ---- the mascot rig ---------------------------------------------------- */
function mountScene(host: HTMLElement, viewBox: string): () => void {
  const scene = buildScene();
  scene.svg.setAttribute("viewBox", viewBox);
  host.appendChild(scene.svg);
  if (reduce) {
    scene.apply(pose(REST_T));
    return () => {};
  }
  let order: BeatOrder = shuffledOrder();
  let t0 = performance.now();
  const tick = (now: number) => {
    scene.apply(pose(now - t0, order));
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
  return () => { order = shuffledOrder(); t0 = performance.now(); };
}

for (const id of ["rig-hero", "rig-cta"]) {
  const host = document.getElementById(id);
  if (!host) continue;
  const replay = mountScene(host, "18 70 288 50");
  if (reduce) host.style.cursor = "default";
  else host.addEventListener("click", replay);
}

const apple = document.getElementById("rig-apple");
if (apple) apple.appendChild(buildAppleScene());

/* ---- the real components ----------------------------------------------- */
startSpinnerTicker();
startElapsedTicker();

const wsHost = document.getElementById("demo-workspace");
if (wsHost) wsHost.appendChild(buildWorkspaceDemo(DEMO_SESSIONS, DEMO_PROJECTS));

const sbHost = document.getElementById("demo-sidebar");
if (sbHost) {
  const wrap = document.createElement("div");
  wrap.className = "sidebar demo-sidebar";
  const scroll = document.createElement("div");
  scroll.className = "sidebar__scroll";
  const listEl = document.createElement("div");
  const newBtn = document.createElement("button");   // unused affordance
  const closedEl = document.createElement("div");
  scroll.append(listEl, closedEl);
  wrap.appendChild(scroll);
  sbHost.appendChild(wrap);
  const sb = new Sidebar(listEl, newBtn, closedEl);
  sb.render(DEMO_SESSIONS, "fix-checkout", [], DEMO_PROJECTS, "projects");
  // Stage the hover: peek the ACTIVE row so the moon renders and its trailing
  // labels bow out — the same trick the app's own harness uses (#projects-sleep).
  wrap.querySelector<HTMLElement>(".session-item--active")
    ?.classList.add("session-item--peek");
}
