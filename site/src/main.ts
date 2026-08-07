// Landing page behavior — "The Windows command center for coding agents".
// Every moving piece is the REAL thing: the hero + CTA wire scenes run the
// rig the app ships (setup-overlay.ts, imported, not copied), the product
// frame is the auto-playing workspace demo built from the app's actual
// components, the sidebar stage mounts the app's Sidebar class, and the
// Windows band runs the apple scene.

import {
  pose, buildScene, shuffledOrder, REST_T,
  type BeatOrder,
} from "../../src/web/src/setup-overlay.js";
import { Sidebar } from "../../src/web/src/sidebar.js";
import { startSpinnerTicker } from "../../src/web/src/spinner.js";
import { startElapsedTicker } from "../../src/web/src/elapsed.js";
import { buildWorkspaceDemo } from "./workspace-demo.js";
import { buildAppleScene } from "./apple-scene.js";

const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;

// One shared spinner + elapsed ticker drives every reused component's braille
// spinner and live "2m"/"now" timestamps (keyed on [data-spinner] / [data-*]).
startSpinnerTicker();
startElapsedTicker();

/* ---- fixtures: the storefront-web scenario -------------------------- */
// Loose objects (esbuild strips types, so only the fields the components read
// at runtime matter). A storefront-web project mid-redesign plus a home-tools
// project, with a warmth spread across the done rows and two slept tabs so
// the Idle drawer shows.
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

/** Mount a live (or reduced-motion static) rig scene into `host`. Returns a
 *  replay hook. The site crops the rig's viewBox to the action band. */
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

/* ---- the wire scenes: hero + closing CTA ----------------------------- */
for (const id of ["rig-hero", "rig-cta"]) {
  const host = document.getElementById(id);
  if (!host) continue;
  const replay = mountScene(host, "18 70 288 50");
  if (reduce) host.style.cursor = "default";
  else host.addEventListener("click", replay);
}

/* ---- hero product frame: the live workspace demo --------------------- */
const wsHost = document.getElementById("demo-workspace");
if (wsHost) wsHost.appendChild(buildWorkspaceDemo(DEMO_SESSIONS, DEMO_PROJECTS));

/* ---- sidebar stage: the REAL Sidebar in projects mode ----------------- */
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

/* ---- the Windows-first beat ------------------------------------------ */
const appleHost = document.getElementById("rig-apple");
if (appleHost) appleHost.appendChild(buildAppleScene());
