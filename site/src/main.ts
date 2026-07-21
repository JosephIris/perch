// Landing page behavior. The hero scene and the boot demo run the SAME rig
// the app ships (imported from setup-overlay.ts, not copied); the widgets are
// small fixture-driven recreations of the real interactions.

import {
  pose, buildScene, shuffledOrder, REST_T,
  type BeatOrder,
} from "../../src/web/src/setup-overlay.js";

const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;

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

/* ---- hero ----------------------------------------------------------- */
const heroHost = document.getElementById("hero-scene");
if (heroHost) {
  const replay = mountScene(heroHost, "20 35 280 100");
  if (reduce) heroHost.style.cursor = "default";
  else heroHost.addEventListener("click", replay);
}

/* ---- widget: session states ----------------------------------------- */
// One live row cycles working -> your turn -> aging (time-lapse), beside two
// static rows showing where the age ramp lands later.
const statesHost = document.getElementById("demo-states");
if (statesHost) {
  const el = (tag: string, cls: string, text = "") => {
    const e = document.createElement(tag);
    e.className = cls;
    if (text) e.textContent = text;
    return e;
  };
  const tag = el("span", "stage-tag", "time-lapse");
  statesHost.appendChild(tag);

  const live = el("div", "srow srow--active");
  const liveLead = el("span", "srow__spin", "");
  const liveTitle = el("span", "srow__title", "fix checkout css");
  const liveMeta = el("span", "srow__meta", "");
  live.append(liveLead, liveTitle, liveMeta);
  statesHost.appendChild(live);

  const mk = (title: string, chip: string, warmth: string) => {
    const r = el("div", "srow");
    r.append(el("span", "srow__dot", ""),
             el("span", "srow__title", title),
             el("span", `srow__chip srow__chip--${warmth}`, chip));
    return r;
  };
  statesHost.append(mk("seo audit pass", "4m", "warm"),
                    mk("invoice export", "22m", "cool"));

  const SPIN = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
  // (phase length ms, render(tick ms into phase))
  const script: Array<[number, (t: number) => void]> = [
    [4500, (t) => {   // working: spinner + honest elapsed
      liveLead.className = "srow__spin";
      liveLead.textContent = reduce ? "•" : SPIN[Math.floor(t / 80) % SPIN.length];
      liveMeta.className = "srow__meta";
      liveMeta.textContent = `editing checkout.css · ${Math.floor(t / 1000)}s`;
    }],
    [1800, () => {    // turn handed back: fresh chip
      liveLead.className = "srow__dot";
      liveLead.textContent = "";
      liveMeta.className = "srow__chip srow__chip--hot";
      liveMeta.textContent = "now";
    }],
    [1800, () => { liveMeta.textContent = "49s"; }],
    [1800, () => {    // the nag warms as it sits
      liveMeta.className = "srow__chip srow__chip--warm";
      liveMeta.textContent = "4m";
    }],
    [1800, () => {
      liveMeta.className = "srow__chip srow__chip--cool";
      liveMeta.textContent = "22m";
    }],
  ];
  const total = script.reduce((s, [ms]) => s + ms, 0);
  const start = performance.now();
  const step = () => {
    let t = (performance.now() - start) % total;
    for (const [ms, render] of script) {
      if (t < ms) { render(t); break; }
      t -= ms;
    }
    if (!reduce) requestAnimationFrame(step);
  };
  if (reduce) script[0][1](2000);
  else requestAnimationFrame(step);
}

/* ---- widget: inspector filters --------------------------------------- */
const idemoHost = document.getElementById("demo-inspector");
if (idemoHost) {
  idemoHost.classList.add("idemo");
  idemoHost.insertAdjacentHTML("beforeend", `
    <span class="stage-tag stage-tag--bottom">try the filters</span>
    <div class="idemo__chips" role="group" aria-label="Filter the journal">
      <button class="ichip" data-all aria-pressed="true">All</button>
      <button class="ichip" data-cat="user" aria-pressed="true">You</button>
      <button class="ichip" data-cat="claude" aria-pressed="true">Claude</button>
      <button class="ichip" data-cat="work" aria-pressed="true">Actions</button>
      <button class="ichip" data-cat="image" aria-pressed="true">Images</button>
    </div>
    <div class="irow irow--user"><span class="irow__glyph">&gt;</span>make the holiday banner match the shop</div>
    <div class="irow irow--claude"><span class="irow__glyph">&#9679;</span>I'll restyle it against the shop tokens.</div>
    <div class="irow irow--work"><span class="irow__glyph">&#9474;</span>Update src/banner.css&nbsp;&nbsp;+12 &#8722;7</div>
    <div class="irow irow--image"><span class="irow__glyph">&#9635;</span>
      <svg width="110" height="34" viewBox="0 0 640 200" aria-label="Banner preview image">
        <rect width="640" height="200" fill="#f6f1e8"/>
        <text x="56" y="102" font-family="Georgia, serif" font-size="46" fill="#1e2a3a">The Holiday Shop</text>
        <rect x="56" y="130" width="132" height="40" rx="20" fill="#b4372f"/>
      </svg></div>
  `);
  const chips = [...idemoHost.querySelectorAll<HTMLButtonElement>(".ichip[data-cat]")];
  const allBtn = idemoHost.querySelector<HTMLButtonElement>(".ichip[data-all]")!;
  const sync = () => {
    for (const c of chips)
      idemoHost.classList.toggle(`idemo--hide-${c.dataset.cat}`,
        c.getAttribute("aria-pressed") !== "true");
    allBtn.setAttribute("aria-pressed",
      String(chips.every((c) => c.getAttribute("aria-pressed") === "true")));
  };
  for (const c of chips)
    c.addEventListener("click", () => {
      c.setAttribute("aria-pressed",
        String(c.getAttribute("aria-pressed") !== "true"));
      sync();
    });
  allBtn.addEventListener("click", () => {
    const target = !chips.every((c) => c.getAttribute("aria-pressed") === "true");
    for (const c of chips) c.setAttribute("aria-pressed", String(target));
    sync();
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
