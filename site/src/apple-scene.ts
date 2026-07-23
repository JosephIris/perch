// The Windows-first brand moment — the SLAPSTICK cut.
//
// Monocle Guy bounces in whistling, clocks the Apple logo, and runs a full
// cartoon TRIPLE-take (look — back — LOOK — back — L O O K), at which point
// his whistled notes shatter mid-air, his eye goes googly-wide against the
// monocle, the monocle itself is blasted clean off his face, and he launches
// into a backwards panic-jump with legs scrambling, crash-landing in a dust
// puff with full squash-and-stretch. Then the camera does a THREE-HIT crash
// zoom (zoom! zoom! ZOOM!) onto the bug-eyed monocled stare, dramatic-
// chipmunk style, and holds while the shock curdles into a flat-browed glare
// and one slow disapproving head shake. Fade, loop.
//
// The bird is the REAL mascot rig (setup-overlay.ts): this scene drives it
// with its own acting track — full Pose frames as a pure function of loop
// time — with the rig's power line hidden and the ground kept flat through
// the `weight` channel. The comedy machinery is layered on top: squash/
// stretch and the airborne jump are composed ONTO the rig's bird group
// post-apply (feet-origin scale keeps him floor-pinned), the googly eye is
// a sclera ellipse injected into the rig's face group with the rig eye
// repurposed as a shrunken jittering pupil, and the monocle ring/chain are
// driven directly (the rig's slip channel stays 0).
//
// LAYOUT CONTRACT: the root is a fixed-aspect stage (aspect-ratio matches
// BAND, overflow hidden) with the svg absolutely filling it. The camera's
// viewBox zoom crops WITHIN that stable box, so the element's layout height
// is constant across the whole loop — walk-in, pancake, and full zoom alike
// — and can never push into the section below.
//
// Timeline (ms; loops at 9000):
//    0-1450   bouncy whistling strut in from the left (notes hover overhead)
// 1450-2150   TRIPLE-take: whip down at the logo / snap front, casual / whip
//             again, faster / snap front, unsettled / final WHIP + freeze
//    2150     SHOCK: notes shatter into shards, eye pops googly, monocle
//             blasted off, anticipation squat
// 2230-2565   the panic JUMP: airborne backwards, legs flailing, stretched,
//             the camera chasing him partway up
// 2565-2900   crash-land: deep squash, dust puffs, camera judder, monocle
//             slaps back on
// 2790-3200   CRASH ZOOM, three hits: 1x -> 1.8x -> 2.9x -> 4.2x (overshoot)
// 3200-5300   the STARE: bug-eye trembling against an oversized monocle,
//             heaving breath, camera creeping in
// 4350        the turn: googly shock curdles into a flat-browed glare
// 5450-6150   slow disapproving head shake ("no. no.") + contempt blink
// 6150-6650   camera pulls back to the wide; the scowl flips to a smug
//             raised brow, beak turns up, he's DONE with this
// 6600-8100   the haughty TROT-OFF: high-step strut out frame-right, leaning
//             back, chin aloft, straight past the apple without a glance
// 8100-8500   the punchline hold: the Apple logo alone on stage
// 8500-9000   fade to nothing, loop

import { pose, buildScene, REST_T } from "../../src/web/src/setup-overlay.js";

/* ---------- staging ---------- */
const LOOP_MS = 9000;
const GROUND_Y = 106.5;              // scene y the feet ride on
const APPLE_X = 228;                 // the logo's spot
const BAND = { x: 30, y: 14, w: 260, h: 104 };  // resting camera frame, 2.5:1
const SHOCK_T = 2150;                // the instant everything goes wrong
const STILL_T = 5000;                // reduced-motion frame: wide glare-off

/* ---------- easing / keyframes ---------- */
const clamp01 = (u: number) => Math.min(1, Math.max(0, u));
const EASE = {
  lin: (u: number) => u,
  in: (u: number) => u * u * u,
  out: (u: number) => 1 - Math.pow(1 - u, 3),
  inout: (u: number) => (u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2),
  back: (u: number) => 1 + 2.6 * Math.pow(u - 1, 3) + 1.6 * Math.pow(u - 1, 2),
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

/* rig wire-sag model solved through `weight` so the (hidden) wire is a floor */
const sag = (x: number) => 92 + 40 * (x / 320) * (1 - x / 320);
const flatWeight = (x: number) => (GROUND_Y - sag(x)) / 1.6;

/* walk-in, brake, the airborne backwards hop, a settle — and finally the
 * haughty trot-off past the apple, exiting frame-right */
const birdX = (t: number) =>
  kf(t, [
    [0, -28, "lin"], [1150, 126, "lin"], [1450, 152, "out"],
    [2225, 152, "lin"], [2530, 127, "out"], [2900, 131, "inout"],
    [6600, 131, "lin"], [8150, 335, "lin"],
  ]);

/* ---------- comedy channels the rig doesn't have ---------- */
/* jump height in bird-local units (feet-origin, so it lifts legs and all) */
const jumpY = (t: number) =>
  kf(t, [[2230, 0], [2390, -11, "out"], [2565, 0, "in"]]);
/* squash-and-stretch: sy tall/short, sx = 1/sy preserves volume */
const squashY = (t: number) =>
  kf(t, [[2140, 1], [2225, 0.74, "out"], [2320, 1.24, "out"], [2530, 1.08, "lin"],
         [2610, 0.68, "out"], [2700, 1.14, "inout"], [2790, 0.95, "inout"], [2880, 1, "inout"]]);

/* ---------- the acting track ---------- */
const BASE = pose(REST_T);   // valid Pose scaffold; every channel overridden

function birdPose(t: number) {
  const x = birdX(t);
  const mv = 1 - clamp01((t - 1120) / 330);          // walking -> stopped
  const stride = (2 * Math.PI * t) / 300;            // peppy little strut
  /* airborne scramble: fast leg/body thrash under a tight envelope */
  const fl = clamp01((t - 2215) / 50) * clamp01((2585 - t) / 70);
  const flail = Math.sin((2 * Math.PI * (t - 2215)) / 95) * fl;
  /* the slow "no. no." head shake, two cycles, extreme close-up scale */
  const shEnv = clamp01((t - 5450) / 80) * clamp01((6150 - t) / 120);
  const headShake = Math.sin((2 * Math.PI * (t - 5450)) / 350) * 1.6 * shEnv;
  /* the haughty trot-off: statelier stride, high-stepping legs, leaning back */
  const tm = clamp01((t - 6550) / 250);
  const trot = (2 * Math.PI * (t - 6550)) / 340;
  return {
    ...BASE,
    noteVis: 0, scribble: 0, jitter: 0, z1: 0, z2: 0, slip: 0,
    x,
    weight: flatWeight(x),
    bob: -Math.abs(Math.sin(stride)) * 2.6 * mv - Math.abs(Math.sin(trot)) * 3.2 * tm,
    legSwing: Math.sin(stride) * 19 * mv + flail * 36 + Math.sin(trot) * 26 * tm,
    /* squat to coil, extend in the air, deep crumple on landing */
    crouch: kf(t, [[1450, 0], [1600, 0.25, "out"], [2100, 0.25, "lin"], [2150, 0.3, "lin"],
                   [2225, 0.85, "out"], [2320, 0, "out"], [2530, 0.1, "lin"],
                   [2615, 0.8, "out"], [2790, 0.45, "inout"], [3100, 0.35, "inout"],
                   [6250, 0.35, "lin"], [6650, 0, "inout"]]),
    bodyRot: 4.5 * mv + flail * 3 + Math.sin(trot - 0.5) * 2 * tm
      + kf(t, [[1500, 0], [1600, 2.5, "out"], [2100, 2.5, "lin"], [2150, 3, "lin"],
               [2225, 6, "out"], [2320, -8, "out"], [2530, -8, "lin"], [2620, -2, "out"],
               [2900, -5, "inout"], [4200, -5, "lin"], [4600, -6.5, "inout"],
               [6250, -6.5, "lin"], [6650, -7.5, "inout"]]),
    /* the TRIPLE-take lives here: whip down / snap front / whip / snap / WHIP */
    headTilt: (-6 + Math.sin(stride - 0.9) * 3) * mv + flail * 3
      + Math.sin(trot - 0.9) * 2 * tm
      + kf(t, [[1500, 0],
               [1590, 15, "out"], [1660, 15, "lin"],       // whip 1: what's that
               [1745, -5, "out"], [1830, -5, "lin"],       // snap front, casual
               [1915, 16, "out"], [1985, 16, "lin"],       // whip 2, faster
               [2055, -3, "out"], [2100, -3, "lin"],       // snap front, unsettled
               [2150, 17, "out"], [2210, 17, "lin"],       // final WHIP + freeze
               [2300, -12, "back"], [2530, -12, "lin"],    // head thrown back in air
               [2650, -6, "out"], [3300, -9, "inout"],
               [5300, -9, "lin"], [5700, -13, "inout"],    // chin creeps higher
               [6250, -13, "lin"], [6650, -16, "inout"]]), // beak fully aloft
    headDX: headShake
      + kf(t, [[1500, 0], [1590, 2.6, "out"], [1660, 2.6, "lin"],
               [1745, -0.8, "out"], [1830, -0.8, "lin"],
               [1915, 3, "out"], [1985, 3, "lin"],
               [2055, -0.8, "out"], [2100, -0.8, "lin"],
               [2150, 3.2, "out"], [2210, 3.2, "lin"],
               [2300, -1.8, "back"], [5300, -1.8, "lin"], [5750, -1.2, "inout"],
               [6600, 0, "inout"]]),
    headDY: kf(t, [[1500, 0], [1590, 1.5, "out"], [1660, 1.5, "lin"],
                   [1745, -0.5, "out"], [1830, -0.5, "lin"],
                   [1915, 1.7, "out"], [1985, 1.7, "lin"],
                   [2055, -0.4, "out"], [2100, -0.4, "lin"],
                   [2150, 1.9, "out"], [2210, 1.9, "lin"],
                   [2300, -1.1, "back"], [3300, -0.8, "inout"],
                   [5300, -0.8, "lin"], [5700, -1.5, "inout"],
                   [6600, -1.1, "inout"]]),
    tailAng: (5 + Math.sin(stride + 1.2) * 4) * mv + 5 * (1 - mv)
      + Math.sin(trot + 1.2) * 3 * tm
      + kf(t, [[2140, 0], [2230, 20, "back"], [2560, 12, "lin"], [2700, 0, "out"],
               [6250, 0, "lin"], [6650, 8, "inout"]]),     // tail carried high
    /* wide pre-shock; bug-eye overrides the middle; narrowed glare; then
     * snooty half-lidded for the trot-off */
    blink: Math.min(
      kf(t, [[2040, 1], [2110, 1.12, "out"], [3900, 1.15, "lin"],
             [4350, 0.55, "inout"], [5300, 0.55, "lin"], [6100, 0.45, "inout"],
             [6600, 0.4, "inout"]]),
      blinkV(t, 600), blinkV(t, 1250), blinkV(t, 1790), blinkV(t, 5450),
      blinkV(t, 6350),
    ),
    /* cheerful -> mild interest -> "hm?" -> concentration -> "wait." -> MAX
     * shock -> (holds through the zoom) -> slams flat when the glare lands
     * -> flips to a smug raise as he turns his beak up and struts */
    brow: kf(t, [[0, 0.5], [1450, 0.5, "lin"],
                 [1560, 0.2, "out"], [1660, 0.2, "lin"],
                 [1750, 0.78, "out"], [1840, 0.78, "lin"],
                 [1930, 0.05, "out"], [2010, 0.05, "lin"],
                 [2070, 0.95, "out"], [2110, 0.95, "lin"],
                 [2160, 1.2, "out"], [3850, 1.15, "lin"],
                 [4350, -0.28, "inout"], [6150, -0.28, "lin"],
                 [6550, 0.85, "inout"]]),
    /* seething breath reads big at 4x; fades once the strut takes over */
    breathe: Math.sin((2 * Math.PI * t) / 2600) *
             kf(t, [[1500, 0], [3300, 0.012, "lin"], [4500, 0.024, "inout"],
                    [6300, 0.024, "lin"], [6900, 0.006, "out"]]),
  };
}

/* ---------- googly eye / flying monocle (driven post-apply) ---------- */
const scleraR = (t: number) =>
  kf(t, [[2150, 0], [2255, 2.2, "back"], [3900, 2.2, "lin"], [4320, 0, "inout"]]);
const pupilR = (t: number) =>
  kf(t, [[2145, 0.95], [2225, 0.4, "out"], [3900, 0.4, "lin"], [4350, 0.95, "inout"]]);
const pupilJit = (t: number) =>
  t < 2260 ? 0 : kf(t, [[2260, 0.3, "lin"], [3600, 0.22, "lin"], [4200, 0, "inout"]]);
const ringDX = (t: number) =>
  kf(t, [[2150, 0], [2255, 1.0, "back"], [2700, 0, "inout"]]);
const ringDY = (t: number) =>
  kf(t, [[2150, 0], [2255, -3.6, "back"], [2480, -2.2, "lin"],
         [2610, 0.6, "out"], [2740, 0, "inout"]]);
const ringR = (t: number) =>   // screwed in tighter + oversized for the stare
  kf(t, [[2780, 1.95], [3190, 2.75, "back"], [3950, 2.75, "lin"], [4400, 1.95, "inout"]]);

/* ---------- camera: three-hit crash zoom, the creep, the pull-back ----------
 * Peak is 4.2x (overshoot via the back ease) creeping to 4.4x — tight on
 * the monocled glare with the head and beak still in frame — then a smooth
 * pull back to the wide for the trot-off. */
function camera(t: number) {
  const z = kf(t, [[2790, 1, "lin"], [2868, 1.8, "out"], [2952, 1.8, "lin"],
                   [3030, 2.9, "out"], [3114, 2.9, "lin"],
                   [3200, 4.2, "back"], [6150, 4.4, "lin"], [6650, 1, "inout"]]);
  let cx = kf(t, [[2790, BAND.x + BAND.w / 2, "lin"], [3200, 148, "back"],
                  [6150, 148, "lin"], [6650, BAND.x + BAND.w / 2, "inout"]]);
  let cy = kf(t, [[2790, BAND.y + BAND.h / 2, "lin"], [3200, 77.5, "back"],
                  [5300, 77.5, "lin"], [5750, 76.5, "inout"],
                  [6150, 76.5, "lin"], [6650, BAND.y + BAND.h / 2, "inout"]]);
  /* the camera chases the panic jump partway (sells the height) and drops
   * back down for the landing */
  if (t > 2230 && t < 2700) {
    cy -= kf(t, [[2230, 0], [2390, 6, "out"], [2600, 0, "in"], [2700, 0, "lin"]]);
    cx -= kf(t, [[2230, 0], [2450, 5, "out"], [2650, 2.5, "inout"], [2700, 0, "out"]]);
  }
  /* impact judder on the crash-landing */
  if (t > 2565 && t < 2870) {
    const d = Math.exp(-(t - 2565) / 120);
    cx += Math.sin((t - 2565) * 0.22) * 3 * d;
    cy += Math.cos((t - 2565) * 0.19) * 1.8 * d;
  }
  const w = BAND.w / z, h = BAND.h / z;
  return { x: cx - w / 2, y: cy - h / 2, w, h };
}

const sceneAlpha = (t: number) =>
  kf(t, [[0, 0], [260, 1, "out"], [8500, 1, "lin"], [9000, 0, "inout"]]);

/* ---------- whistled notes: hover overhead, then SHATTER at the shock ---------- */
const NOTE_SPAWN = [350, 760, 1170];
const NOTE_GLYPHS = ["♪", "♫", "♪"];
const SCATTER_VX = [-30, 6, 34];
const SCATTER_VY = [-26, -40, -20];
const SCATTER_SPIN = [-540, 620, -480];

/* pure pre-shock note position, so the scatter can re-query the freeze frame */
function notePos(t: number, k: number) {
  const sp = NOTE_SPAWN[k];
  const ur = clamp01((t - sp) / 520);
  const bx = birdX(sp);
  return {
    x: bx + 36 + (k - 1) * 4 + ur * 3,
    y: GROUND_Y - 25 - 20 * EASE.out(ur) + Math.sin((2 * Math.PI * (t - sp)) / 1500 + k * 1.3) * 1.7 * ur,
    rot: Math.sin(t * 0.006 + k * 2) * 12,
    op: 0.9 * clamp01((t - sp) / 160),
  };
}

export function buildAppleScene(): HTMLElement {
  const wrap = document.createElement("div");
  wrap.className = "apple-scene-stage";

  /* visually self-contained: scoped style injected once.
   * The stage is a FIXED-aspect box (matching BAND) with overflow hidden
   * and the svg absolutely filling it — the camera's viewBox zoom crops
   * within this stable box, so the scene's layout height is constant
   * across the whole loop and can never push into the section below. */
  if (!document.getElementById("apple-scene-stage-style")) {
    const st = document.createElement("style");
    st.id = "apple-scene-stage-style";
    st.textContent =
      `.apple-scene-stage{position:relative;display:block;width:100%;` +
      `aspect-ratio:${BAND.w}/${BAND.h};overflow:hidden;color:var(--ink,#e9eef4)}` +
      `.apple-scene-stage svg{position:absolute;inset:0;display:block;` +
      `width:100%;height:100%}`;
    document.head.appendChild(st);
  }

  const scene = buildScene();
  const svg = scene.svg;

  /* no wire in this scene — the rig appends it first; hide that node */
  (svg.firstElementChild as SVGElement).style.display = "none";

  /* grab rig internals BEFORE adding our own shapes (construction order:
   * wire, pad, bird, z, z — and inside the face: brow, eye, ring, chain) */
  const birdG = svg.children[2] as SVGGElement;
  const eye = svg.querySelector("ellipse") as SVGEllipseElement;
  const ring = svg.querySelector("circle") as SVGCircleElement;
  const face = eye.parentElement as unknown as SVGGElement;
  const chain = face.querySelectorAll("path")[1] as SVGPathElement;

  const NS = "http://www.w3.org/2000/svg";
  const el = <T extends SVGElement>(n: string, at: Record<string, string>): T => {
    const e = document.createElementNS(NS, n) as T;
    for (const k in at) e.setAttribute(k, at[k]);
    return e;
  };

  /* googly sclera lives INSIDE the rig's face group, under the pupil */
  const sclera = el<SVGEllipseElement>("ellipse", {
    cx: "17", cy: "9", rx: "0", ry: "0",
    fill: "#fff", stroke: "#15233b", "stroke-width": "0.28",
  });
  sclera.style.visibility = "hidden";
  face.insertBefore(sclera, eye);

  /* scenery: contact shadows + THE Apple logo (parody — flat Mac aluminum,
   * the bitten silhouette with its little detached leaf) + landing dust */
  const scenery = el<SVGGElement>("g", {});
  const birdShadow = el<SVGEllipseElement>("ellipse", {
    cy: String(GROUND_Y + 2.6), rx: "15", ry: "2.1", fill: "#000", opacity: "0.17",
  });
  const appleShadow = el<SVGEllipseElement>("ellipse", {
    cx: String(APPLE_X), cy: String(GROUND_Y + 2.4), rx: "10.5", ry: "1.9",
    fill: "#000", opacity: "0.2",
  });
  const APPLE_S = 1.1;
  const apple = el<SVGGElement>("g", {
    transform: `translate(${(APPLE_X - 12.1 * APPLE_S).toFixed(1)} ` +
      `${(GROUND_Y + 2.2 - 24 * APPLE_S).toFixed(1)}) scale(${APPLE_S})`,
  });
  apple.innerHTML =
    `<path fill="#b9bec6" d="M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.03 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701"/>`;
  const dustEls = [0, 1, 2].map(() => {
    const c = el<SVGCircleElement>("circle", { fill: "currentColor" });
    c.style.visibility = "hidden";
    return c;
  });
  scenery.append(birdShadow, appleShadow, apple, ...dustEls);
  svg.insertBefore(scenery, birdG);

  /* notes + shatter shards float above everything */
  const ACCENT = "var(--color-accent, var(--accent, #76B9ED))";
  const noteEls = NOTE_GLYPHS.map((g, i) => {
    const n = el<SVGTextElement>("text", {
      "font-size": i === 1 ? "9.5" : "8",
      "font-family": `"Segoe UI Symbol", sans-serif`,
      fill: ACCENT, "text-anchor": "middle",
    });
    n.textContent = g;
    n.style.visibility = "hidden";
    svg.appendChild(n);
    return n;
  });
  const shardEls: SVGLineElement[] = [];
  for (let i = 0; i < 9; i++) {
    const s = el<SVGLineElement>("line", {
      x1: "-1", y1: "0", x2: "1", y2: "0",
      stroke: ACCENT, "stroke-width": "0.7", "stroke-linecap": "round",
    });
    s.style.visibility = "hidden";
    svg.appendChild(s);
    shardEls.push(s);
  }

  const setVB = (r: { x: number; y: number; w: number; h: number }) =>
    svg.setAttribute("viewBox",
      `${r.x.toFixed(2)} ${r.y.toFixed(2)} ${r.w.toFixed(2)} ${r.h.toFixed(2)}`);

  function drawFrame(t: number, still = false): void {
    const p = birdPose(t);
    scene.apply(p);

    /* squash-and-stretch + the airborne lift, composed onto the rig's bird
     * group (feet are the group origin, so scale pins him to the floor) */
    const sy = squashY(t);
    const jy = jumpY(t);
    if (sy !== 1 || jy !== 0) {
      birdG.setAttribute("transform",
        `${birdG.getAttribute("transform")} translate(0 ${jy.toFixed(2)})` +
        ` scale(${(1 / sy).toFixed(3)} ${sy.toFixed(3)})`);
    }

    /* ground shadow: follows him, shrinks and fades while airborne */
    const air = clamp01(-jy / 8);
    birdShadow.setAttribute("cx", p.x.toFixed(2));
    birdShadow.setAttribute("rx", ((15 + p.bob * 0.9) * (1 - 0.5 * air)).toFixed(2));
    birdShadow.setAttribute("opacity", (0.17 * (1 - 0.65 * air)).toFixed(3));

    /* googly bug-eye: sclera bulges against the monocle, rig eye becomes a
     * tiny jittering pupil */
    const scR = scleraR(t);
    if (scR > 0.02) {
      sclera.style.visibility = "visible";
      sclera.setAttribute("rx", scR.toFixed(2));
      sclera.setAttribute("ry", (scR * 0.96).toFixed(2));
      const pr = pupilR(t), ja = pupilJit(t);
      eye.setAttribute("rx", pr.toFixed(2));
      eye.setAttribute("ry", pr.toFixed(2));
      eye.setAttribute("cx", (17 + Math.sin(t * 0.043) * ja).toFixed(2));
      eye.setAttribute("cy", (9 + Math.cos(t * 0.037) * ja * 0.8).toFixed(2));
    } else {
      sclera.style.visibility = "hidden";
      eye.setAttribute("rx", "0.95");
      eye.setAttribute("cx", "17");
      eye.setAttribute("cy", "9");
    }

    /* the monocle: blasted off on the shock, slaps back, then oversized */
    const rdx = ringDX(t), rdy = ringDY(t);
    ring.setAttribute("transform", `translate(${rdx.toFixed(2)} ${rdy.toFixed(2)})`);
    ring.setAttribute("r", ringR(t).toFixed(2));
    chain.setAttribute("transform",
      `translate(${(rdx * 0.55).toFixed(2)} ${(rdy * 0.45).toFixed(2)})`);

    /* notes: hover while whistling; at SHOCK_T they scatter ballistically
     * with spin, plus a burst of shards — the whistle literally shatters */
    for (let k = 0; k < noteEls.length; k++) {
      const n = noteEls[k];
      if (still || t < NOTE_SPAWN[k] + 30) { n.style.visibility = "hidden"; continue; }
      if (t < SHOCK_T) {
        const q = notePos(t, k);
        n.style.visibility = "visible";
        n.setAttribute("x", q.x.toFixed(1));
        n.setAttribute("y", q.y.toFixed(1));
        n.setAttribute("transform", `rotate(${q.rot.toFixed(1)} ${q.x.toFixed(1)} ${q.y.toFixed(1)})`);
        n.style.opacity = q.op.toFixed(3);
      } else {
        const u = (t - SHOCK_T) / 430;
        if (u >= 1) { n.style.visibility = "hidden"; continue; }
        const b = notePos(SHOCK_T, k);
        const nx = b.x + SCATTER_VX[k] * u;
        const ny = b.y + SCATTER_VY[k] * u + 26 * u * u;
        n.style.visibility = "visible";
        n.setAttribute("x", nx.toFixed(1));
        n.setAttribute("y", ny.toFixed(1));
        n.setAttribute("transform",
          `rotate(${(SCATTER_SPIN[k] * u).toFixed(0)} ${nx.toFixed(1)} ${ny.toFixed(1)})`);
        n.style.opacity = (0.9 * (1 - u * u)).toFixed(3);
      }
    }
    const us = (t - SHOCK_T) / 300;
    for (let i = 0; i < shardEls.length; i++) {
      const s = shardEls[i];
      if (still || us <= 0 || us >= 1) { s.style.visibility = "hidden"; continue; }
      const k = Math.floor(i / 3);
      const b = notePos(SHOCK_T, k);
      const ang = ((k * 40 + (i % 3) * 120 + 20) * Math.PI) / 180;
      const d = 10 * EASE.out(us);
      const sx = b.x + Math.cos(ang) * d;
      const syp = b.y - 2 - Math.sin(ang) * d;
      s.style.visibility = "visible";
      s.setAttribute("transform",
        `translate(${sx.toFixed(1)} ${syp.toFixed(1)}) rotate(${((ang * 180) / Math.PI).toFixed(0)})`);
      s.style.opacity = (0.85 * (1 - us)).toFixed(3);
    }

    /* dust puffs on the crash-landing */
    const ud = (t - 2600) / 360;
    for (let i = 0; i < dustEls.length; i++) {
      const c = dustEls[i];
      if (still || ud <= 0 || ud >= 1) { c.style.visibility = "hidden"; continue; }
      const spread = [-9, 0, 9][i] * (0.5 + 0.8 * ud);
      c.style.visibility = "visible";
      c.setAttribute("cx", (birdX(2600) + spread).toFixed(1));
      c.setAttribute("cy", (GROUND_Y + 0.5 - 2.5 * ud - (i === 1 ? 1.5 : 0)).toFixed(1));
      c.setAttribute("r", (1.2 + 2.2 * ud).toFixed(2));
      c.style.opacity = (0.3 * (1 - ud)).toFixed(3);
    }

    setVB(still ? BAND : camera(t));
    wrap.style.opacity = still ? "1" : sceneAlpha(t).toFixed(3);
  }

  wrap.appendChild(svg);

  if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
    drawFrame(STILL_T, true);   // wide shot: mid-glare stand-off with the logo
    return wrap;
  }

  /* ?scene-t=<ms> freezes the loop at that frame for deterministic captures */
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
