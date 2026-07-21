// Per-pane "Setting up…" overlay — a frosted cover shown while a Claude Code
// pane boots, then hidden. It replaces the old trick of typing `/color` into
// cc's raw PTY (fragile: it raced cc's input reader and could concatenate onto
// whatever the user had already typed). Instead we cover the pane during the
// boot window so nothing lands in cc — a plain blur frost, no tint, no glow.
//
// The mascot ("Monocle Guy") walks in along a sagging power line, lands — the
// wire takes a damped bounce — perches, and then runs a three-beat idle
// routine: an inquisitive look-around, a note-taking beat (a little pad hangs
// on the wire; he scribbles at it beak-first and lines appear), and a sleepy
// beat (lids sink, the monocle slips, z's drift up, he nods off and startles
// awake). The beat ORDER is shuffled per show() — boots are short, so a fixed
// order meant nobody ever saw past the first beat. The whole performance is
// pose(t, order), a pure function of its arguments → rig parameters, driven
// by one rAF loop per visible overlay. Any frame is reproducible by number
// and order, which is how it was reviewed: design-loop/perch-wire-mockup.html
// is the scrubbable source of truth (?order=bca to pin a permutation) — keep
// the two in sync when iterating.

const W = 320;                     // scene viewBox
const H = 150;
const S = 1.6;                     // bird rig scale (art authored small, shown big)
const PERCH_X = 175;               // where the bird settles (slightly right of center)
const WALK_FROM = 46;              // enters from the left
const NOTE_X = 216;                // where the note pad hangs, just right of the perch
const INTRO_MS = 2050;             // walk + land
const IDLE_A_MS = 3600;            // beat 1: inquisitive look-around
const IDLE_B_MS = 4600;            // beat 2: taking notes
const IDLE_C_MS = 5600;            // beat 3: drowse, nod off, startle awake
const IDLE_MS = IDLE_A_MS + IDLE_B_MS + IDLE_C_MS;  // full routine, then repeats
const REST_T = 2450;               // perched pose used when motion is reduced
const FADE_OUT_MS = 150;           // hide() fade — matches --dur-fast in tokens.css

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
  /* prop / beat channels (zero outside their beat) */
  noteVis: number;   // note pad visibility 0..1
  scribble: number;  // how much of the pad's writing exists 0..1
  jitter: number;    // enveloped scribble oscillation (head + pad wobble)
  z1: number; z2: number;  // sleepy "z" glyph phases, live in (0,1)
  slip: number;      // monocle slip 0..1
}

/* channels only some beats drive; zero everywhere else */
const CALM = { noteVis: 0, scribble: 0, jitter: 0, z1: 0, z2: 0, slip: 0 };

/* shared perched baseline the idle beats override from */
const PERCHED = {
  ...CALM,
  x: PERCH_X, bob: 0, legSwing: 0, crouch: 0.55, weight: 2.9,
  bodyRot: 0, headTilt: 0, headDX: 0, headDY: 0,
  tailAng: 4, blink: 1, breathe: 0, brow: 0.25,
};

/* ---------- pose(t): every number the rig needs ----------
 * `order` permutes the idle beats. Boots are short — with a fixed order
 * nobody would ever meet the note-taker or the sleeper — so each show()
 * shuffles. Every beat starts AND ends at the neutral perched pose, so any
 * permutation chains seamlessly. The order is an argument (not hidden
 * state), keeping pose a pure function of (t, order): same inputs, same
 * frame, so the mockup can still scrub and captures stay reproducible. */
type BeatOrder = readonly [number, number, number];
const CANONICAL_ORDER: BeatOrder = [0, 1, 2];

function pose(t: number, order: BeatOrder = CANONICAL_ORDER): Pose {
  if (t < INTRO_MS) return intro(t);
  const BEATS = [
    { ms: IDLE_A_MS, at: idleInquisitive },
    { ms: IDLE_B_MS, at: idleNotes },
    { ms: IDLE_C_MS, at: idleSleepy },
  ];
  let i = (t - INTRO_MS) % IDLE_MS;
  for (const bi of order) {
    if (i < BEATS[bi].ms) return BEATS[bi].at(i);
    i -= BEATS[bi].ms;
  }
  return idleInquisitive(0);   // unreachable: the beat lengths sum to IDLE_MS
}

function shuffledOrder(): BeatOrder {
  const p = [0, 1, 2];
  for (let i = p.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [p[i], p[j]] = [p[j], p[i]];
  }
  return p as unknown as BeatOrder;
}

/* -------- intro: walk in, brake, land into the perch -------- */
function intro(t: number): Pose {
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
    ...CALM,
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

/* -------- beat 1: perched, inquisitive -------- */
function idleInquisitive(i: number): Pose {
  return {
    ...PERCHED,
    breathe: Math.sin(2 * Math.PI * (2 * i / IDLE_A_MS)) * 0.012, // 2 cycles/beat → seamless
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

/* -------- beat 2: taking notes -------- */
/* A little pad fades in hanging on the wire beside him; he leans over it and
   scribbles beak-first in two bursts (lines appear as he writes), with a
   raised-brow think-glance up between them, then a satisfied tail flick. */
function idleNotes(n: number): Pose {
  /* write bursts: a fast small oscillation under a trapezoid envelope — the
     same value drives the head jiggle and the pad's clothesline swing */
  const env =
    clamp01((n - 600) / 120) * clamp01((1450 - n) / 120) +
    clamp01((n - 2600) / 120) * clamp01((3400 - n) / 120);
  const jitter = env * Math.sin(2 * Math.PI * n / 115);
  return {
    ...PERCHED,
    breathe: Math.sin(2 * Math.PI * (2 * n / IDLE_B_MS)) * 0.012,
    noteVis: kf(n, [[150, 0], [550, 1, "out"], [4050, 1, "lin"], [4500, 0, "inout"]]),
    scribble: kf(n, [[600, 0], [1450, 0.5, "lin"], [2600, 0.5], [3400, 1, "lin"]]),
    jitter,
    headTilt: kf(n, [[0, 0], [500, 14, "inout"], [1450, 14, "lin"],
                     [1800, -8, "back"], [2250, -8, "lin"],       // hmm… (thinks)
                     [2600, 14, "inout"], [3400, 14, "lin"],
                     [4100, 0, "inout"]]) + jitter * 1.6,
    headDX: kf(n, [[0, 0], [500, 2.2, "inout"], [1450, 2.2, "lin"],
                   [1800, -0.6, "back"], [2250, -0.6, "lin"],
                   [2600, 2.2, "inout"], [3400, 2.2, "lin"], [4100, 0, "inout"]]),
    headDY: kf(n, [[0, 0], [500, 1.6, "inout"], [1450, 1.6, "lin"],
                   [1800, -1.0, "back"], [2250, -1.0, "lin"],
                   [2600, 1.6, "inout"], [3400, 1.6, "lin"], [4100, 0, "inout"]]) + jitter * 0.35,
    bodyRot: kf(n, [[0, 0], [500, 4, "inout"], [3400, 4, "lin"], [4100, 0, "inout"]]),
    tailAng: kf(n, [[3400, 4], [3560, 15, "back"], [3900, 4, "out"]]), // done — flick
    blink: Math.min(blink(n, 2050), blink(n, 3650)),
    brow: kf(n, [[0, 0.25], [500, -0.2, "inout"], [1450, -0.2, "lin"], // concentrating
                 [1800, 0.9, "back"], [2250, 0.9, "lin"],
                 [2600, -0.2, "inout"], [3400, -0.2, "lin"],
                 [3700, 0.6, "back"], [4600, 0.25, "inout"]]),         // satisfied
  };
}

/* -------- beat 3: sleepy -------- */
/* Lids sink, the head and body sag, the monocle slips, z's drift up. A slow
   nod-off tips too far and he startles awake — wide-eyed, monocle snapped
   back — which hands off cleanly to the inquisitive beat on loop. */
function idleSleepy(s: number): Pose {
  /* deep-drowse micro-sway on the lids so the doze doesn't freeze */
  const sway = s > 1500 && s < 3700 ? Math.sin(2 * Math.PI * (s - 1500) / 1100) * 0.06 : 0;
  const lids = kf(s, [[0, 1], [1500, 0.45, "inout"], [3100, 0.3, "lin"],
                      [3700, 0.12, "out"], [4000, 0.12, "lin"],
                      [4180, 1.05, "back"],                      // startle: eyes wide
                      [4900, 1, "lin"]]);
  return {
    ...PERCHED,
    /* slower, deeper breathing while dozing; 1.5 cycles/beat keeps sin() zero
       at both edges so the neighbours join seamlessly */
    breathe: Math.sin(2 * Math.PI * (1.5 * s / IDLE_C_MS)) *
             kf(s, [[0, 0.012], [1500, 0.024, "inout"], [4000, 0.024, "lin"], [4400, 0.012, "out"]]),
    headTilt: kf(s, [[0, 0], [1500, 9, "inout"], [3100, 12, "lin"],
                     [3700, 18, "out"], [4000, 18, "lin"],       // the nod-off
                     [4180, -5, "back"], [4900, -5, "lin"],      // SNAP awake
                     [5600, 0, "inout"]]),
    headDY: kf(s, [[0, 0], [1500, 1.6, "inout"], [3700, 2.4, "lin"], [4000, 2.4, "lin"],
                   [4180, -0.8, "back"], [4900, -0.8, "lin"], [5600, 0, "inout"]]),
    headDX: kf(s, [[0, 0], [1500, 0.6, "inout"], [3700, 1.0, "lin"], [4000, 1.0, "lin"],
                   [4180, -0.4, "back"], [4900, -0.4, "lin"], [5600, 0, "inout"]]),
    bodyRot: kf(s, [[0, 0], [1500, 2.5, "inout"], [3700, 4, "lin"], [4000, 4, "lin"],
                    [4180, -1, "back"], [4900, -1, "lin"], [5600, 0, "inout"]]),
    crouch: 0.55 + kf(s, [[0, 0], [1500, 0.25, "inout"], [4000, 0.25, "lin"],
                          [4180, -0.1, "back"], [4900, -0.1, "lin"], [5600, 0, "inout"]]),
    weight: 2.9 + kf(s, [[0, 0], [1500, 0.4, "inout"], [4000, 0.4, "lin"], [4400, 0, "out"]]),
    tailAng: kf(s, [[0, 4], [1600, 1, "inout"], [4000, 1, "lin"],
                    [4180, 14, "back"], [4600, 4, "out"]]),
    blink: Math.min(lids + sway, blink(s, 5150)),                // one settling re-blink
    slip: kf(s, [[0, 0], [1600, 0.6, "inout"], [3700, 1, "lin"], [4000, 1, "lin"],
                 [4230, 0, "back"]]),
    z1: (s - 2000) / 1300,
    z2: (s - 2700) / 1200,
    brow: kf(s, [[0, 0.25], [1500, -0.3, "inout"], [3700, -0.45, "lin"], [4000, -0.45, "lin"],
                 [4180, 1, "back"], [4900, 1, "lin"], [5600, 0.25, "inout"]]),
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

  /* note pad — clipped to the wire beside the perch, hidden outside beat 2.
     Local origin is the attachment point on the wire so the scribble wobble
     swings it clothesline-style. Ink matches the eye/brow ink; the paper is
     currentColor like the silhouette (the art is light-on-dark by design). */
  const pad = el<SVGGElement>("g", {});
  const padInk = { fill: "none", stroke: "#15233b", "stroke-linecap": "round" };
  pad.append(
    el<SVGPathElement>("path", { d: "M0 0 V 2.2", stroke: "currentColor",
      "stroke-width": "1.1", "stroke-linecap": "round", fill: "none" }),
    el<SVGRectElement>("rect", { x: "-8.5", y: "2.2", width: "17", height: "13",
      rx: "1.5", fill: "currentColor", opacity: "0.92" }),
  );
  const padLine1 = el<SVGPathElement>("path", {
    ...padInk, "stroke-width": "0.9", pathLength: "1",
    "stroke-dasharray": "1", "stroke-dashoffset": "1",
    d: "M-5.3 6.8 C -3.9 5.9, -2.7 7.5, -1.3 6.7 C 0.1 6.0, 1.4 7.4, 2.8 6.6 C 3.9 6.0, 4.7 6.9, 5.3 6.7",
  });
  const padLine2 = el<SVGPathElement>("path", {
    ...padInk, "stroke-width": "0.9", pathLength: "1",
    "stroke-dasharray": "1", "stroke-dashoffset": "1",
    d: "M-5.3 10.8 C -3.9 9.9, -2.7 11.5, -1.3 10.7 C -0.1 10.1, 1.0 11.0, 1.9 10.8",
  });
  pad.append(padLine1, padLine2);
  pad.style.visibility = "hidden";
  svg.appendChild(pad);

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

  /* sleepy z's — drift up-right from the head, hidden outside beat 3 */
  const mkZ = (fs: number): SVGTextElement => {
    const z = el<SVGTextElement>("text", {
      "font-size": String(fs), "font-style": "italic", "font-weight": "600",
      fill: "currentColor", "text-anchor": "middle",
    });
    z.textContent = "z";
    z.style.visibility = "hidden";
    svg.appendChild(z);
    return z;
  };
  const zBig = mkZ(9);
  const zSmall = mkZ(7);

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

    /* monocle slip — rides the face group, so this is drift relative to the eye */
    const slipT = `translate(${(p.slip * 0.25).toFixed(2)} ${(p.slip * 0.55).toFixed(2)})`;
    ring.setAttribute("transform", slipT);
    chain.setAttribute("transform", slipT);

    /* note pad: hangs from the wire, drops in as it fades, swings on scribble */
    if (p.noteVis > 0.01) {
      const py = wireY(NOTE_X, p.x, p.weight) + (1 - p.noteVis) * 2.5;
      pad.style.visibility = "visible";
      pad.style.opacity = p.noteVis.toFixed(3);
      pad.setAttribute("transform",
        `translate(${NOTE_X} ${py.toFixed(2)}) rotate(${(p.jitter * 2.5).toFixed(2)})`);
      padLine1.setAttribute("stroke-dashoffset", (1 - clamp01(p.scribble * 2)).toFixed(3));
      padLine2.setAttribute("stroke-dashoffset", (1 - clamp01(p.scribble * 2 - 1)).toFixed(3));
    } else {
      pad.style.visibility = "hidden";
    }

    /* z's: phase 0..1 = fade in, drift up-right off the head, fade out */
    const hx = p.x + 19, hy = gy - 27;   // head (eye) in scene coordinates
    for (const [zel, ph] of [[zBig, p.z1], [zSmall, p.z2]] as const) {
      if (ph > 0 && ph < 1) {
        zel.style.visibility = "visible";
        zel.setAttribute("x", (hx + 6 + ph * 9).toFixed(1));
        zel.setAttribute("y", (hy - 6 - ph * 13).toFixed(1));
        zel.style.opacity = (Math.sin(Math.PI * ph) * 0.7).toFixed(3);
      } else {
        zel.style.visibility = "hidden";
      }
    }
  }
  return { svg, apply };
}

/* Shared with site/ (the landing page hero renders the same bird from the
 * same source, so the demo can't drift from the app). Nothing else should
 * need these. */
export { pose, buildScene, shuffledOrder, CANONICAL_ORDER, REST_T, INTRO_MS, IDLE_MS };
export type { BeatOrder, Scene };

export interface SetupOverlay {
  /** The root element to append to the pane. */
  readonly el: HTMLElement;
  /** Reveal the cover and start the performance from the walk-in. */
  show(): void;
  /** Fade the cover out quickly, then stop the animation loop. */
  hide(): void;
}

/** Build a hidden setup overlay for a pane. Caller appends `el` to the pane
 *  root, calls show()/hide(), and manages focus. `shuffleIdle` is on for the
 *  app (each boot meets a different beat first) and off where a capture must
 *  be reproducible (the harness hero shot). */
export function createSetupOverlay(shuffleIdle = true): SetupOverlay {
  const el = document.createElement("div");
  el.className = "setup-overlay";
  el.tabIndex = -1;
  el.hidden = true;
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
  let hideTimer = 0;
  let runOrder: BeatOrder = CANONICAL_ORDER;

  function tick(now: number): void {
    scene.apply(pose(now - t0, runOrder));
    raf = requestAnimationFrame(tick);
  }

  function stop(): void {
    el.classList.remove("setup-overlay--closing");
    el.hidden = true;
    if (raf) { cancelAnimationFrame(raf); raf = 0; }
  }

  return {
    el,
    show() {
      if (hideTimer) { window.clearTimeout(hideTimer); hideTimer = 0; }
      el.classList.remove("setup-overlay--closing");
      el.hidden = false;
      runOrder = shuffleIdle ? shuffledOrder() : CANONICAL_ORDER;
      if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        // The STATE still reads (perched bird + caption); only motion drops.
        // Canonical order on purpose: the static frame is the reviewed one.
        scene.apply(pose(REST_T));
        return;
      }
      if (raf) cancelAnimationFrame(raf);
      t0 = performance.now();
      raf = requestAnimationFrame(tick);
    },
    hide() {
      if (el.hidden || hideTimer) return;
      if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        stop();
        return;
      }
      // Quick fade of the whole cover; the rig keeps animating through it so
      // the bird doesn't freeze mid-gesture while the frost lifts.
      el.classList.add("setup-overlay--closing");
      hideTimer = window.setTimeout(() => {
        hideTimer = 0;
        stop();
      }, FADE_OUT_MS + 30);
    },
  };
}
