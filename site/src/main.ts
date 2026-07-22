// Landing page behavior. The hero scene and the boot demo run the SAME rig
// the app ships (imported from setup-overlay.ts, not copied); the widgets are
// small fixture-driven recreations of the real interactions.

import {
  pose, buildScene, shuffledOrder, REST_T,
  type BeatOrder,
} from "../../src/web/src/setup-overlay.js";
// The REAL app chrome, driven by fixtures — exact replicas, not recreations.
import { Sidebar } from "../../src/web/src/sidebar.js";
import { startSpinnerTicker } from "../../src/web/src/spinner.js";
import { startElapsedTicker } from "../../src/web/src/elapsed.js";
import { buildInspector } from "./inspector-demo.js";
import { buildWorkspaceDemo } from "./workspace-demo.js";
import { buildAppleScene } from "./apple-scene.js";

const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;

// One shared spinner + elapsed ticker drives every reused component's braille
// spinner and live "2m"/"now" timestamps (keyed on [data-spinner] / [data-*]).
startSpinnerTicker();
startElapsedTicker();

/* ---- fixtures: the storefront-web scenario -------------------------- */
// Loose objects (esbuild strips types, so only the fields the components read
// at runtime matter). Mirrors the product screenshot: a storefront-web project
// mid-redesign plus a home-tools project, all at rest.
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

/* ---- hero interactive workspace demo -------------------------------- */
const heroDemoHost = document.getElementById("hero-demo");
if (heroDemoHost) heroDemoHost.appendChild(buildWorkspaceDemo(DEMO_SESSIONS, DEMO_PROJECTS));

const appleHost = document.getElementById("apple-scene");
if (appleHost) appleHost.appendChild(buildAppleScene());

/* ---- hero ----------------------------------------------------------- */
const heroHost = document.getElementById("hero-scene");
if (heroHost) {
  const replay = mountScene(heroHost, "18 70 288 50");
  if (reduce) heroHost.style.cursor = "default";
  else heroHost.addEventListener("click", replay);
}

/* ---- widget: session states (REAL Sidebar) -------------------------- */
// Mount the app's actual Sidebar with fixture sessions — exact rendering, not
// a recreation. Projects mode shows the nested worktree tabs, live spinners on
// working rows, the warmth-decaying "done" ages, and the ↑N ready-to-push chip.
const statesHost = document.getElementById("demo-states");
if (statesHost) {
  const wrap = document.createElement("div");
  wrap.className = "sidebar demo-sidebar";
  const scroll = document.createElement("div");
  scroll.className = "sidebar__scroll";
  const listEl = document.createElement("div");
  const newBtn = document.createElement("button");   // unused affordance
  const closedEl = document.createElement("div");
  scroll.append(listEl, closedEl);
  wrap.appendChild(scroll);
  statesHost.appendChild(wrap);

  const sb = new Sidebar(listEl, newBtn, closedEl);
  sb.render(DEMO_SESSIONS, "holiday-banner", [], DEMO_PROJECTS, "projects");
}

/* ---- widget: inspector journal (REAL classes, hand-built) ------------ */
// The app's exact journal markup (turn-prompt / beat / work / imgrow / changes
// / inspector__filter), styled by the app's real style.css. Filters are live.
const idemoHost = document.getElementById("demo-inspector");
if (idemoHost) {
  const rail = buildInspector({ changesOpen: false });
  idemoHost.appendChild(rail);
  // Scroll the stream so a banner preview is in view — image-in-a-terminal is
  // the card's whole point.
  requestAnimationFrame(() => {
    const stream = rail.querySelector<HTMLElement>(".inspector__stream");
    const img = rail.querySelector<HTMLElement>(".imgrow");
    if (stream && img) stream.scrollTop = Math.max(0, img.offsetTop - 44);
  });
}

/* ---- widget: boot cover ---------------------------------------------- */
const bootHost = document.getElementById("demo-boot");
if (bootHost) {
  bootHost.insertAdjacentHTML("beforeend", `
    <span class="stage-tag">click to uncover</span>
    <div class="bootpane__head">
      <span class="bootpane__tag"></span>gift guide draft
      <span class="bootpane__badge">CC</span>
    </div>
    <div class="bootpane__body">
      <div class="bootpane__term">PS C:\\dev\\storefront-web&gt; claude<br>&nbsp;<span class="bootpane__cursor"></span></div>
      <div class="bootpane__cover">
        <div class="bootpane__stack">
          <div class="bootpane__scene"></div>
          <div class="bootpane__caption">Setting up&hellip;</div>
        </div>
      </div>
    </div>
  `);
  const sceneHost = bootHost.querySelector<HTMLElement>(".bootpane__scene")!;
  const cover = bootHost.querySelector<HTMLElement>(".bootpane__cover")!;
  const replay = mountScene(sceneHost, "60 45 200 85");
  bootHost.addEventListener("click", () => {
    const uncovering = !cover.classList.contains("bootpane__cover--off");
    cover.classList.toggle("bootpane__cover--off", uncovering);
    if (!uncovering) replay();   // covering again = a fresh boot
  });
}
