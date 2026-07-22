// The Windows-first brand moment: Monocle Guy strolls in whistling, spots an
// apple (a Mac stand-in), and the camera pushes in on his disgust before the
// scene fades and loops.
//
// The bird is the REAL mascot rig (setup-overlay.ts) — same silhouette, same
// brow/blink/monocle channels — but the rig's own beats are wire-perch
// theatre, so this scene drives the rig with its own acting track: we build
// full Pose frames per-frame as a pure function of loop time, exactly like
// the rig does. The rig's power line is hidden (no wire in this scene) and
// the ground is kept flat by solving the rig's wire-sag model through the
// `weight` channel (weight only feeds bird-y once the wire is invisible).
// The camera is a per-frame viewBox interpolation on the same SVG, so the
// push-in crops at the frame edge like a real lens instead of scaling a div.
//
// Timeline (ms; loops at 7600):
//    0-2600   walks in from the left, jaunty, whistling (notes off the beak)
// 2600-3650   pulls up; cranes at the apple; squints... wait a minute
// 3650-3950   the POP: recoil hop, monocle jolt, eyes wide
// 3950-6900   camera pushes into his face; brow slams flat (ugh, a Mac),
//             an indignant shudder, then the "hmph": eyes shut, beak aloft
// 6900-7600   fade to nothing, loop

import { pose, buildScene, REST_T } from "../../src/web/src/setup-overlay.js";

/* ---------- staging ---------- */
const LOOP_MS = 7600;
const GROUND_Y = 106.5;              // scene y the feet ride on
const APPLE_X = 230;                 // apple's spot, right of the confrontation
const BAND = { x: 30, y: 52, w: 260, h: 66 };   // resting camera frame
const STILL_T = 5450;                // reduced-motion frame: mid-glare

/* ---------- easing / keyframes (mirrors the rig's kf) ---------- */
const clamp01 = (u: number) => Math.min(1, Math.max(0, u));
const EASE = {
  lin: (u: number) => u,
  out: (u: number) => 1 - Math.pow(1 - u, 3),
  inout: (u: number) => (u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2),
  back: (u: number) => 1 + 2.4 * Math.pow(u - 1, 3) + 1.4 * Math.pow(u - 1, 2),
};
type Key = [number, number, (keyof typeof EASE)?];
function kf(t: number, keys: Key[]): number {
  if (t <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i++) {
    const [t1, v1, e] = keys[i];
    const [t0, v0] = keys[i - 1];
    if (t <= t1) return v0 + (v1 - v0) * EASE[e ?? "inout"](clamp01((t - t0) / (t1 - t0)));
  }
  return keys[keys.length - 1][1];
}
const blinkV = (t: number, at: number) => {
  const d = Math.abs(t - at);
  return d > 70 ? 1 : Math.max(0.12, d / 70);
};

/* The rig places the bird at wire height: sag(x) + weight * birdScale. With
 * the wire hidden, weight has no other visible effect, so solve it per-x to
 * pin the feet to a flat floor. */
const sag = (x: number) => 92 + 40 * (x / 320) * (1 - x / 320);
const flatWeight = (x: number) => (GROUND_Y - sag(x)) / 1.6;

/* walk-in x: enters offscreen-left, strides across, brakes; a small hop-back
 * recoil on the realization pop. Pure in t, so note spawns can re-query it. */
const birdX = (t: number) =>
  kf(t, [
    [0, -26, "lin"], [2150, 124, "lin"], [2600, 150, "out"],
    [3650, 150, "lin"], [3760, 142, "back"], [4400, 146, "inout"],
  ]);

/* ---------- the acting track: a full rig Pose for any loop time ---------- */
const BASE = pose(REST_T);   // valid Pose scaffold; every channel is overridden

function birdPose(t: number) {
  const x = birdX(t);
  const mv = 1 - clamp01((t - 2150) / 450);          // walking → stopped
  const stride = (2 * Math.PI * t) / 420;
  const settled = clamp01((t - 2600) / 400);
  /* indignant shudder during the glare */
  const shud = t > 4900 && t < 5280
    ? Math.sin((2 * Math.PI * (t - 4900)) / 70) * clamp01((t - 4900) / 60) * clamp01((5280 - t) / 90)
    : 0;
  return {
    ...BASE,
    noteVis: 0, scribble: 0, jitter: 0, z1: 0, z2: 0,
    x,
    weight: flatWeight(x),
    bob: -Math.abs(Math.sin(stride)) * 1.8 * mv
       + kf(t, [[3640, 0], [3730, -3.4, "out"], [3940, 0, "inout"]]),   // startle hop
    legSwing: Math.sin(stride) * 17 * mv,
    crouch: kf(t, [[2450, 0], [2650, 0.22, "out"], [3620, 0.18, "lin"],
                   [3700, 0.5, "back"], [4300, 0.15, "inout"]]),
    bodyRot: 4.5 * mv + kf(t, [[2800, 0], [3100, 3, "inout"], [3620, 3, "lin"],
                               [3720, -2, "back"], [4450, -4, "inout"], [5600, -4, "lin"],
                               [6050, -6.5, "inout"]]),
    /* negative tilt = chin up. Jaunty chin-up walk → crane down at the apple
     * → recoil → disdain chin-up → full "hmph" beak-aloft */
    headTilt: (-6 + Math.sin(stride - 0.9) * 3) * mv
      + kf(t, [[2750, 0], [3080, 13, "inout"], [3620, 13, "lin"],
               [3720, -6, "back"], [4150, -6, "lin"], [4450, -10, "inout"],
               [5550, -10, "lin"], [6050, -17, "inout"]])
      + shud * 1.2,
    headDX: kf(t, [[2750, 0], [3080, 2.2, "inout"], [3620, 2.2, "lin"],
                   [3720, -1.6, "back"], [4450, -1.8, "inout"], [5600, -1.8, "lin"],
                   [6050, -1.2, "inout"]]),
    headDY: kf(t, [[2750, 0], [3080, 1.2, "inout"], [3620, 1.2, "lin"],
                   [3720, -0.8, "back"], [4450, -0.6, "inout"], [5600, -0.6, "lin"],
                   [6050, -1.6, "inout"]]) + shud * 0.25,
    tailAng: (5 + Math.sin(stride + 1.2) * 3) * mv
      + kf(t, [[2600, 0], [3000, 5, "out"], [3550, 5, "lin"], [3700, 19, "back"],
               [4200, 6, "out"], [6250, 6, "lin"], [6400, 16, "back"], [6800, 6, "out"]]) * settled,
    /* squint at it... eyes POP wide... one slow contempt blink... shut for the
     * hmph. Walk blinks mixed in v-shaped, like the rig. */
    blink: Math.min(
      kf(t, [[3150, 1], [3350, 0.5, "inout"], [3600, 0.5, "lin"], [3670, 1.18, "back"],
             [4300, 1.05, "lin"], [4800, 1, "lin"], [5250, 0.78, "inout"],
             [5550, 0.78, "lin"], [5950, 0.1, "inout"]]),
      blinkV(t, 900), blinkV(t, 1800), blinkV(t, 5150),
    ),
    /* the personality channel: happy-raised on the stroll, "hm?" flicker,
     * shock-raise on the pop, then slammed flat and judgy */
    brow: 0.55 * mv
      + kf(t, [[2600, 0], [3050, 0.15, "inout"], [3550, 0.15, "lin"], [3660, 1.05, "back"],
               [4050, 1.05, "lin"], [4450, -0.42, "inout"], [5900, -0.42, "lin"],
               [6150, -0.3, "inout"]]) * settled
      + (mv > 0 && mv < 1 ? 0.15 * mv : 0),
    breathe: Math.sin((2 * Math.PI * t) / 2600) * 0.014 * settled,
    slip: kf(t, [[3640, 0], [3760, 0.75, "out"], [4150, 0, "inout"]]),  // monocle jolt
  };
}

/* ---------- camera: fast push onto the face, then a slow menace creep ---------- */
function camera(t: number) {
  const z = kf(t, [[3950, 1], [4800, 2.02, "inout"], [6900, 2.42, "lin"]]);
  const cx = kf(t, [[3950, BAND.x + BAND.w / 2, "lin"], [4800, 167, "inout"]]);
  const cy = kf(t, [[3950, BAND.y + BAND.h / 2, "lin"], [4800, 78, "inout"]]);
  const w = BAND.w / z, h = BAND.h / z;
  return { x: cx - w / 2, y: cy - h / 2, w, h };
}

const sceneAlpha = (t: number) =>
  kf(t, [[0, 0], [350, 1, "out"], [6900, 1, "lin"], [7600, 0, "inout"]]);

/* ---------- whistled notes: spawn on the stroll, hang in the world ---------- */
const NOTE_SPAWN = [480, 1130, 1780];
const NOTE_DUR = 1050;
const NOTE_GLYPHS = ["♪", "♫", "♪"];

export function buildAppleScene(): HTMLElement {
  const wrap = document.createElement("div");
  wrap.className = "apple-scene";

  const scene = buildScene();
  const svg = scene.svg;
  svg.classList.add("apple-scene__bird");

  /* no wire in this scene — the rig appends it first, so hide that node */
  (svg.firstElementChild as SVGElement).style.display = "none";

  const NS = "http://www.w3.org/2000/svg";
  const el = <T extends SVGElement>(n: string, at: Record<string, string>): T => {
    const e = document.createElementNS(NS, n) as T;
    for (const k in at) e.setAttribute(k, at[k]);
    return e;
  };

  /* scenery under the bird: contact shadows + the apple (silver, bitten) */
  const scenery = el<SVGGElement>("g", {});
  const birdShadow = el<SVGEllipseElement>("ellipse", {
    cy: String(GROUND_Y + 2.6), rx: "15", ry: "2.1", fill: "#000", opacity: "0.17",
  });
  const appleShadow = el<SVGEllipseElement>("ellipse", {
    cx: String(APPLE_X), cy: String(GROUND_Y + 2.4), rx: "11", ry: "1.9",
    fill: "#000", opacity: "0.2",
  });
  const apple = el<SVGGElement>("g", {
    transform: `translate(${APPLE_X - 36 * 0.34} ${GROUND_Y + 2 - 72 * 0.34}) scale(0.34)`,
  });
  apple.innerHTML =
    `<path d="M36 22 C 23 9, 7 16, 9 33 C 11 51, 23 72, 36 72 C 49 72, 61 51, 63 33 C 65 16, 49 9, 36 22 Z" fill="#c4c8ce"/>` +
    `<path d="M45 17 C 50 8, 59 6, 61 4 C 59 13, 53 19, 46 20 Z" fill="#9aa0a8"/>` +
    `<path d="M36 22 C 36 17, 37 13, 39 10" stroke="#7c8189" stroke-width="2.6" fill="none" stroke-linecap="round"/>` +
    `<path d="M63 33 C 56 30, 51 36, 53 43 C 55 49, 62 49, 63 41 Z" fill="#131316"/>`;
  scenery.append(birdShadow, appleShadow, apple);
  svg.insertBefore(scenery, svg.children[1]);   // above the hidden wire, below the bird

  /* the whistled notes float above everything */
  const noteEls = NOTE_GLYPHS.map((g, i) => {
    const n = el<SVGTextElement>("text", {
      "font-size": i === 1 ? "9.5" : "8",
      "font-family": `"Segoe UI Symbol", sans-serif`,
      fill: "var(--color-accent)", "text-anchor": "middle",
    });
    n.textContent = g;
    n.style.visibility = "hidden";
    svg.appendChild(n);
    return n;
  });

  const setVB = (r: { x: number; y: number; w: number; h: number }) =>
    svg.setAttribute("viewBox",
      `${r.x.toFixed(2)} ${r.y.toFixed(2)} ${r.w.toFixed(2)} ${r.h.toFixed(2)}`);

  function drawFrame(t: number, still = false): void {
    const p = birdPose(t);
    scene.apply(p);
    birdShadow.setAttribute("cx", p.x.toFixed(2));
    birdShadow.setAttribute("rx", (15 + p.bob * 0.9).toFixed(2));

    for (let i = 0; i < noteEls.length; i++) {
      const ph = (t - NOTE_SPAWN[i]) / NOTE_DUR;
      if (!still && ph > 0 && ph < 1) {
        /* off the beak, then up fast enough that the walk doesn't catch them */
        const nx = birdX(NOTE_SPAWN[i]) + 41 + ph * 12;
        const ny = GROUND_Y - 26.5 - 22 * EASE.out(ph);
        noteEls[i].style.visibility = "visible";
        noteEls[i].setAttribute("x", nx.toFixed(1));
        noteEls[i].setAttribute("y", ny.toFixed(1));
        noteEls[i].setAttribute("transform",
          `rotate(${(Math.sin(ph * Math.PI * 3) * 9).toFixed(1)} ${nx.toFixed(1)} ${ny.toFixed(1)})`);
        noteEls[i].style.opacity = (Math.sin(Math.PI * ph) * 0.9).toFixed(3);
      } else {
        noteEls[i].style.visibility = "hidden";
      }
    }

    setVB(still ? BAND : camera(t));
    wrap.style.opacity = still ? "1" : sceneAlpha(t).toFixed(3);
  }

  wrap.appendChild(svg);

  if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
    drawFrame(STILL_T, true);   // already met the apple; visibly unimpressed
    return wrap;
  }

  /* scrub hook: ?scene-t=4600 freezes the loop at that ms, like the rig's
   * design-loop mockups — every reviewed frame is reproducible by number */
  const scrub = new URLSearchParams(location.search).get("scene-t");
  if (scrub !== null) {
    drawFrame(((+scrub % LOOP_MS) + LOOP_MS) % LOOP_MS);
    return wrap;
  }

  const t0 = performance.now();
  const tick = (now: number) => {
    drawFrame((now - t0) % LOOP_MS);
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
  return wrap;
}
