// Per-pane "Setting up…" overlay — a frosted cover shown while a Claude Code
// pane boots, then hidden. It replaces the old trick of typing `/color` into
// cc's raw PTY (fragile: it raced cc's input reader and could concatenate onto
// whatever the user had already typed). Instead we cover the pane during the
// boot window so nothing lands in cc, and the frost tints to the pane's color.
//
// The mascot ("Monocle Guy") walks in along a sagging power line, lands — the
// wire takes a damped bounce — perches, and loops an inquisitive idle: a smug
// raised-brow chin-up, then a flat-browed peer down toward the pane. The whole
// performance is pose(t), a pure function of milliseconds → rig parameters,
// driven by one rAF loop per visible overlay. Any frame is reproducible by
// number, which is how it was reviewed: design-loop/perch-wire-mockup.html is
// the scrubbable source of truth — keep the two in sync when iterating.

const W = 320;                     // scene viewBox
const H = 150;
const S = 1.6;                     // bird rig scale (art authored small, shown big)
const PERCH_X = 175;               // where the bird settles (slightly right of center)
const WALK_FROM = 46;              // enters from the left
const INTRO_MS = 2050;             // walk + land
const IDLE_MS = 3600;              // inquisitive loop
const REST_T = 2450;               // perched pose used when motion is reduced

/* ---------- easing ---------- */
const clamp01 = (u: number) => Math.min(1, Math.max(0, u));
const easeOutCubic = (u: number) => 1 - Math.pow(1 - u, 3);
const easeInOutCubic = (u: number) =>
  u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2;
const easeOutBack = (u: number) => {
  const c = 1.4;
  return 1 + (c + 1) * Math.pow(u - 1, 3) + c * Math.pow(u - 1, 2);
};
const EASES = { lin: (u: number) => u, out: easeOutCubic, inout: easeInOutCubic, back: easeOutBack };

type Key = [number, number, (keyof typeof EASES)?];

/** keyframe track — the ease names the curve INTO that key; clamped at both ends */
function kf(t: number, keys: Key[]): number {
  if (t <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i++) {
    const [t1, v1, e] = keys[i];
    const [t0, v0] = keys[i - 1];
    if (t <= t1) {
      const u = EASES[e ?? "inout"](clamp01((t - t0) / (t1 - t0)));
      return v0 + (v1 - v0) * u;
    }
  }
  return keys[keys.length - 1][1];
}

/* wire: gentle catenary-ish sag across the frame + a local dip under the bird */
const wireBaseY = (x: number) => 92 + 40 * (x / W) * (1 - x / W);
function wireY(x: number, birdX: number, weight: number): number {
  const d = (x - birdX) / 38;
  return wireBaseY(x) + weight * S * Math.exp(-d * d);
}

/* blink: v-shaped closure around a center time, ~140ms total */
function blink(t: number, at: number): number {
  const d = Math.abs(t - at);
  return d > 70 ? 1 : Math.max(0.12, d / 70);
}

interface Pose {
  x: number; bob: number; legSwing: number; bodyRot: number; headTilt: number;
  crouch: number; tailAng: number; weight: number; blink: number;
  breathe: number; headDX: number; headDY: number; brow: number;
}

/* ---------- pose(t): every number the rig needs ---------- */
function pose(t: number): Pose {
  if (t < INTRO_MS) {
    /* -------- intro: walk in, brake, land into the perch -------- */
    const walkEnd = 1550;
    const stride = 2 * Math.PI * (t / 430);
    const moving = t < walkEnd ? 1 - clamp01((t - 1250) / 300) : 0;
    const crouch = kf(t, [[1450, 0], [1650, 1, "out"], [2050, 0.55]]);
    let weight = 2.0 + crouch * 1.6;
    if (t > 1600) {
      const tau = (t - 1600) / 1000;
      weight += 1.4 * Math.exp(-3.2 * tau) * Math.cos(12 * tau);
    }
    return {
      x: kf(t, [[0, WALK_FROM], [walkEnd, PERCH_X, "out"]]),
      bob: -Math.abs(Math.sin(stride)) * 1.7 * moving,
      legSwing: Math.sin(stride) * 16 * moving,
      bodyRot: 4 * moving + kf(t, [[1250, 0], [1550, -3, "out"], [1900, 0]]),
      headTilt: Math.sin(stride - 0.9) * 2.5 * moving,
      crouch,
      tailAng: kf(t, [[1500, 0], [1680, 16, "back"], [2050, 4]]),
      weight,
      blink: blink(t, 950),
      breathe: 0,
      headDX: 0,
      headDY: 0,
      brow: kf(t, [[1450, 0], [1700, 1, "back"], [2050, 0.25]]), // pops on landing
    };
  }
  /* -------- idle: perched, inquisitive -------- */
  const i = (t - INTRO_MS) % IDLE_MS;
  return {
    x: PERCH_X,
    bob: 0,
    legSwing: 0,
    crouch: 0.55,
    weight: 2.9,
    breathe: Math.sin(2 * Math.PI * (2 * i / IDLE_MS)) * 0.012, // 2 cycles/loop → seamless
    headTilt: kf(i, [
      [0, 0],
      [350, -19, "back"],   // curious chin-up tilt
      [1100, -19, "lin"],
      [1500, 11, "inout"],  // then peers down toward the pane
      [2500, 11, "lin"],
      [3000, 0, "inout"],
      [3600, 0],
    ]),
    /* the tilt alone barely reads — translate the face too: a lift-back on the
       chin-up, a forward-down crane on the peer */
    headDX: kf(i, [[0, 0], [350, -0.8, "back"], [1100, -0.8, "lin"], [1500, 1.7, "inout"], [2500, 1.7, "lin"], [3000, 0, "inout"]]),
    headDY: kf(i, [[0, 0], [350, -0.9, "back"], [1100, -0.9, "lin"], [1500, 1.1, "inout"], [2500, 1.1, "lin"], [3000, 0, "inout"]]),
    bodyRot: kf(i, [[1100, 0], [1500, 3.5, "inout"], [2500, 3.5], [3000, 0, "inout"]]),
    tailAng: kf(i, [[2100, 4], [2260, 15, "back"], [2600, 4, "out"]]),
    blink: Math.min(blink(i, 800), blink(i, 2050)),
    /* the personality channel: raised + smug on the chin-up tilt,
       flattened + judgy on the peer-down */
    brow: kf(i, [[0, 0.25], [350, 1, "back"], [1100, 1, "lin"],
                 [1500, -0.35, "inout"], [2500, -0.35, "lin"], [3000, 0.25, "inout"]]),
  };
}

/* ================================================================
   Rig. Mascot geometry is the original "Monocle Guy" silhouette —
   one merged path, so the rig is trunk-rotation + an animated face.
   ================================================================ */
const K = 2.5;                          // mascot-units → rig-units
const FEET = { x: 12.35, y: 15.7 };     // midpoint between the mascot's feet
const BODY_D = "M19.9 10.0 C 19.5 7.9, 17.6 6.3, 15.4 6.5 C 13.9 6.5, 12.5 6.8, 11.4 7.4 C 9.4 7.6, 7.4 6.2, 5.7 6.7 C 5.1 6.9, 5.2 7.6, 5.9 8.1 C 7.0 8.9, 7.7 10.1, 8.5 11.1 C 9.6 12.7, 10.9 14.4, 12.6 14.4 C 14.0 14.4, 15.2 13.9, 16.1 13.0 C 17.1 12.4, 18.0 11.9, 18.6 11.2 C 19.1 10.9, 19.6 10.6, 19.9 10.0 Z";
const BEAK_D = "M17.4 9.4 L 21.9 10.3 L 18.2 11.35 Z";

/* brow: lerp between neutral / raised (r=1, smug) / flat (r<0, judgy) */
const BROW_N = [15.9, 7.3, 16.55, 7.25, 17.3, 7.5, 17.9, 8.0];
const BROW_R = [15.75, 6.55, 16.6, 6.15, 17.45, 6.35, 18.0, 7.15];
function browD(r: number): string {
  const p = BROW_N.map((v, i) => v + (BROW_R[i] - v) * r);
  return `M${p[0].toFixed(2)} ${p[1].toFixed(2)} C ${p[2].toFixed(2)} ${p[3].toFixed(2)}, ${p[4].toFixed(2)} ${p[5].toFixed(2)}, ${p[6].toFixed(2)} ${p[7].toFixed(2)}`;
}

interface Scene {
  svg: SVGSVGElement;
  apply(p: Pose): void;
}

function buildScene(): Scene {
  const NS = "http://www.w3.org/2000/svg";
  const el = <T extends SVGElement>(n: string, at: Record<string, string>): T => {
    const e = document.createElementNS(NS, n) as T;
    for (const k in at) e.setAttribute(k, at[k]);
    return e;
  };

  const svg = el<SVGSVGElement>("svg", { viewBox: `0 0 ${W} ${H}`, "aria-hidden": "true" });
  svg.classList.add("setup-scene-svg");

  const wire = el<SVGPathElement>("path", {
    fill: "none", stroke: "currentColor",
    "stroke-width": "1.6", "stroke-linecap": "round", opacity: ".65",
  });
  svg.appendChild(wire);

  const bird = el<SVGGElement>("g", {});
  /* everything below lives in mascot coordinates, feet mapped to (0,0) */
  const mascot = el<SVGGElement>("g", {
    transform: `scale(${K}) translate(${-FEET.x} ${-FEET.y})`,
  });

  const legL = el<SVGPathElement>("path", {
    d: "M11.3 14.1 V 15.7", stroke: "currentColor",
    "stroke-width": "1.0", "stroke-linecap": "round", fill: "none",
  });
  const legR = el<SVGPathElement>("path", {
    d: "M13.4 14.1 V 15.7", stroke: "currentColor",
    "stroke-width": "1.0", "stroke-linecap": "round", fill: "none",
  });

  const trunk = el<SVGGElement>("g", {});      // pitch / tilt / crouch / bob
  const breatheG = el<SVGGElement>("g", {});
  const body = el<SVGPathElement>("path", { d: BODY_D, fill: "currentColor" });
  const beak = el<SVGPathElement>("path", { d: BEAK_D, fill: "currentColor" });

  const face = el<SVGGElement>("g", {});       // peek translation rides here
  const brow = el<SVGPathElement>("path", {
    d: browD(0), fill: "none", stroke: "#15233b",
    "stroke-width": ".75", "stroke-linecap": "round",
  });
  const eye = el<SVGEllipseElement>("ellipse", { cx: "17", cy: "9", rx: ".95", ry: ".95", fill: "#15233b" });
  const ring = el<SVGCircleElement>("circle", {
    cx: "17", cy: "9", r: "1.95", fill: "none",
    stroke: "var(--color-accent)", "stroke-width": ".6",
  });
  const chain = el<SVGPathElement>("path", {
    d: "M17.6 10.8 C 17.8 11.7, 17.5 12.5, 16.9 13.1",
    fill: "none", stroke: "var(--color-accent)", "stroke-width": ".5", "stroke-linecap": "round",
  });
  face.append(brow, eye, ring, chain);

  breatheG.append(body, beak, face);
  trunk.appendChild(breatheG);
  mascot.append(legL, legR, trunk);
  bird.appendChild(mascot);
  svg.appendChild(bird);

  function apply(p: Pose): void {
    /* wire re-sampled each frame so it dips under the bird */
    let d = "";
    for (let x = 0; x <= W; x += 8)
      d += (x ? " L" : "M") + x.toFixed(1) + " " + wireY(x, p.x, p.weight).toFixed(2);
    wire.setAttribute("d", d);

    const gy = wireY(p.x, p.x, p.weight);
    bird.setAttribute("transform",
      `translate(${p.x.toFixed(2)} ${gy.toFixed(2)}) scale(${S})`);

    /* rig values were authored in rig-units; /K converts into mascot space */
    const dy = (p.bob + p.crouch * 2.2) / K;
    const tilt = p.bodyRot + p.headTilt * 0.55 - p.tailAng * 0.12;
    trunk.setAttribute("transform",
      `translate(0 ${dy.toFixed(3)}) rotate(${tilt.toFixed(2)} 13 11.5)`);
    breatheG.setAttribute("transform",
      `translate(${FEET.x} ${FEET.y}) scale(1 ${(1 + p.breathe).toFixed(4)})` +
      ` translate(${-FEET.x} ${-FEET.y})`);

    legL.setAttribute("transform", `rotate(${(+p.legSwing).toFixed(1)} 11.3 14.1)`);
    legR.setAttribute("transform", `rotate(${(-p.legSwing).toFixed(1)} 13.4 14.1)`);

    face.setAttribute("transform",
      `translate(${(p.headDX * 0.35).toFixed(2)} ${(p.headDY * 0.35).toFixed(2)})`);
    brow.setAttribute("d", browD(p.brow));
    eye.setAttribute("ry", (0.95 * p.blink).toFixed(2));
  }
  return { svg, apply };
}

export interface SetupOverlay {
  /** The root element to append to the pane. */
  readonly el: HTMLElement;
  /** Tint the frost to a pane color-tag index (0..5). */
  setColor(colorIndex: number): void;
  /** Reveal the cover and start the performance from the walk-in. */
  show(): void;
  /** Hide the cover and stop the animation loop. */
  hide(): void;
}

/** Build a hidden setup overlay for a pane. Caller appends `el` to the pane
 *  root, calls show()/hide(), and manages focus. */
export function createSetupOverlay(): SetupOverlay {
  const el = document.createElement("div");
  el.className = "setup-overlay";
  el.tabIndex = -1;
  el.hidden = true;
  // The tint glow lives on __art, not on the overlay root: the root's center is
  // the PANE's center, but the art sits above it (the stack is svg + caption),
  // so a root-anchored gradient always read high and off to one side of the
  // bird. Anchored here it tracks the art no matter how the pane is sized.
  el.innerHTML =
    `<div class="setup-overlay__stack">` +
    `<div class="setup-overlay__art"></div>` +
    `<div class="setup-overlay__caption">Setting up` +
    `<span class="setup-overlay__dots"></span></div></div>`;

  const scene = buildScene();
  el.querySelector(".setup-overlay__art")!.appendChild(scene.svg);

  // Swallow keystrokes while up so nothing leaks past the cover (the terminal
  // is blurred by the pane, but this guards stray page-level defaults too).
  el.addEventListener("keydown", (e) => {
    e.preventDefault();
    e.stopPropagation();
  });

  let raf = 0;
  let t0 = 0;

  function tick(now: number): void {
    scene.apply(pose(now - t0));
    raf = requestAnimationFrame(tick);
  }

  return {
    el,
    setColor(colorIndex: number) {
      const i = ((colorIndex % 6) + 6) % 6;
      el.style.setProperty("--setup-hint", `var(--color-pane-tag-${i})`);
    },
    show() {
      el.hidden = false;
      if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        // The STATE still reads (perched bird + caption); only motion drops.
        scene.apply(pose(REST_T));
        return;
      }
      if (raf) cancelAnimationFrame(raf);
      t0 = performance.now();
      raf = requestAnimationFrame(tick);
    },
    hide() {
      el.hidden = true;
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
    },
  };
}
