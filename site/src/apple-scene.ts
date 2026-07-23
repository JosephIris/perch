// Variant C — INSTANT-MENACE with a strut-off payoff.
//
// Monocle Guy saunters in whistling, chin up, notes floating off his beak —
// a bird without a care. He clocks the Apple logo and it registers in a
// single sharp beat: eyes narrow, brow drains, and the camera EXPLODES into
// a dramatic-chipmunk extreme close-up of his monocled glare — brow slammed,
// lens glinting, breath heaving. He holds the fury... whips his beak into
// the air, and STRUTS off frame right — high knees, eyes shut, tail flick
// as he passes the logo without a glance — leaving the apple alone on
// stage. Loop.
//
// Same idiom as apple-scene.ts (per-frame Pose acting track, viewBox
// camera, flat ground via the weight channel, ?scene-t scrub) but its own
// acting and props (the monocle glint — his monocle never slips; he is far
// too composed for that) and its own exit payoff.
//
// Timeline (ms; loops at 6250):
//    0-1400   carefree whistling saunter in from the left; brakes at the mark
// 1400-1650   INSTANT recognition: quick crane down, eyes narrow, brow
//             drains — one sharp beat, no lingering
// 1650-1910   SNAP ZOOM 1x -> 5x, brow slams to fury, eyes pop, camera
//             shakes off the punch, monocle glint sweep
// 1910-3550   the MENACE hold: camera creeps to 5.3x, head lowers, eyes
//             narrow, fury breathing, a rage tremble right at the peak
// 3550-3900   whip zoom-out; simultaneously the SNUB: beak whips aloft,
//             eyes slam shut, body rocks back
// 3900-5600   the STRUT-OFF: high-step prance out frame RIGHT, eyes
//             closed the whole way, tail flick as he passes the apple
// 5600-6250   the apple sits there, alone, judged; fade, loop

import { pose, buildScene, REST_T } from "../../src/web/src/setup-overlay.js";

/* ---------- staging ---------- */
const LOOP_MS = 6250;
const GROUND_Y = 106.5;             // scene y the feet ride on
const APPLE_X = 230;                // the logo's spot, right of the stop mark
const STOP_X = 140;                 // where he plants for the glare
/* Resting camera frame. 2.5:1 — tall enough that the walk-in has sky for
 * the whistled notes and the close-up has real headroom on mobile. The
 * stage div is FIXED to this aspect (see injected style) with the svg
 * filling it, so the viewBox zoom crops inside a stable box and the
 * scene's layout height never changes mid-loop. */
const BAND = { x: 30, y: 22, w: 260, h: 104 };
const STILL_T = 1600;               // reduced-motion frame: just clocked it

/* ---------- easing / keyframes (same shapes the rig uses) ---------- */
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

/* The rig seats the bird at wire height sag(x) + weight * birdScale; with the
 * wire hidden, weight is free — solve it per-x so the feet ride a flat floor. */
const sag = (x: number) => 92 + 40 * (x / 320) * (1 - x / 320);
const flatWeight = (x: number) => (GROUND_Y - sag(x)) / 1.6;

/* walk-in from offscreen left; brakes at the mark; a haughty little back-rock
 * wind-up at 3950; then the strut carries him out past the apple, offscreen
 * right. Pure in t so shadows and note spawns can re-query it. */
const birdX = (t: number) =>
  kf(t, [
    [0, -30, "lin"], [1150, 126, "lin"], [1400, STOP_X, "out"],
    [3950, STOP_X, "lin"], [4130, 133, "inout"],
    [5600, 348, "lin"],
  ]);

/* ---------- the acting track: a full rig Pose for any loop time ---------- */
const BASE = pose(REST_T);   // valid Pose scaffold; every channel overridden

function birdPose(t: number) {
  const x = birdX(t);
  const mv1 = 1 - clamp01((t - 1150) / 260);       // walk-in -> stopped
  const mv2 = clamp01((t - 4130) / 200);           // stopped -> strut-out
  const stride1 = (2 * Math.PI * t) / 340;
  const stride2 = (2 * Math.PI * (t - 4130)) / 380;
  const settled = clamp01((t - 1400) / 300) * (1 - mv2);
  /* rage tremble right at the peak of the hold, just before the snub */
  const trem = t > 3200 && t < 3530
    ? Math.sin((2 * Math.PI * (t - 3200)) / 70) * clamp01((t - 3200) / 80) * clamp01((3530 - t) / 90)
    : 0;
  /* fury breathing — visible heave at 4.5x zoom */
  const fury = clamp01((t - 1950) / 400) * clamp01((3550 - t) / 250);
  return {
    ...BASE,
    noteVis: 0, scribble: 0, jitter: 0, z1: 0, z2: 0,
    slip: 0,                       // the monocle NEVER moves. He is composed.
    x,
    weight: flatWeight(x),
    bob: -Math.abs(Math.sin(stride1)) * 2 * mv1
      + kf(t, [[1670, 0], [1760, -2.6, "out"], [1970, 0, "inout"],
               [3800, 0, "lin"], [3890, -2.2, "out"], [4080, 0, "inout"]])
      - Math.abs(Math.sin(stride2)) * 3.4 * mv2,   // prancing strut bounce
    legSwing: Math.sin(stride1) * 17 * mv1
      + Math.sin(stride2) * 24 * mv2,              // exaggerated high-step
    crouch: kf(t, [[1250, 0], [1450, 0.3, "out"], [1670, 0.22, "inout"],
                   [1790, 0.34, "out"],                                // coil on the snap
                   [3550, 0.34, "lin"], [3850, 0.02, "back"], [4210, 0.1, "inout"]]),
    bodyRot: 4.5 * mv1
      + kf(t, [[1400, 0], [1560, 2.5, "out"], [1630, 2.5, "lin"],
               [1800, 1, "out"], [3350, 2.2, "lin"], [3550, 2.2, "lin"],
               [3850, -7, "back"], [4300, -6, "inout"]])               // leans BACK, snooty
      + Math.sin(stride2) * 1.6 * mv2,
    /* negative tilt = chin up. Chin-up whistling saunter -> fast crane at
     * the logo -> slow menacing head-lower through the glare -> beak-aloft
     * snub whip -> stays aloft the whole strut */
    headTilt: (-8 + Math.sin(stride1 - 0.9) * 2.5) * mv1
      + kf(t, [[1320, 0], [1480, 12, "out"], [1650, 12, "lin"],
               [1800, 4.5, "out"], [2250, 5, "lin"], [3350, 8.5, "lin"],
               [3550, 8.5, "lin"], [3850, -25, "back"], [4300, -20, "inout"]])
      + Math.sin(stride2 - 0.9) * 2 * mv2
      + trem * 1.1,
    headDX: kf(t, [[1320, 0], [1480, 2.6, "out"], [1650, 2.7, "lin"],
                   [2250, 2.4, "lin"], [3350, 3.4, "lin"],             // creeping closer
                   [3550, 3.4, "lin"], [3850, -2.2, "back"], [4350, -1.4, "inout"]])
      + trem * 0.3,
    headDY: kf(t, [[1320, 0], [1480, 1.5, "out"], [1650, 1.5, "lin"],
                   [1800, 0.8, "out"], [3350, 1.6, "lin"],
                   [3550, 1.6, "lin"], [3850, -1.8, "back"], [4350, -1.3, "inout"]]),
    tailAng: (5 + Math.sin(stride1 + 1.2) * 3) * mv1
      + kf(t, [[1400, 0], [1600, 6, "out"], [3730, 6, "lin"],
               [3880, 18, "back"], [4250, 9, "out"],                   // flick on the snub
               [4710, 9, "lin"], [4850, 17, "back"], [5210, 9, "out"]])// flick passing the apple
      + Math.sin(stride2 + 1.2) * 3 * mv2,
    /* the lids: a hard narrowing squint the instant he clocks it, a
     * pop-wide on the snap, a long narrowing through the hold, then
     * slammed shut for the entire strut */
    blink: Math.min(
      kf(t, [[500, 1], [1440, 1, "lin"],
             [1560, 0.55, "out"],                  // sees it. eyes NARROW. instantly.
             [1640, 0.55, "lin"],
             [1760, 1.18, "back"],                 // POP on the snap
             [2050, 1.08, "lin"], [3300, 0.6, "lin"],  // narrowing menace
             [3550, 0.6, "lin"], [3820, 0.1, "inout"]]),  // shut. done. leaving.
      blinkV(t, 620),
    ),
    /* brow: pleased whistling saunter -> drains flat the instant he
     * registers -> SLAMMED to fury -> deeper at the peak -> maximum smug
     * for the exit */
    brow: kf(t, [[0, 0.55], [1400, 0.55, "lin"], [1540, 0.1, "out"],
                 [1660, 0.1, "lin"],
                 [1780, -0.5, "back"], [3150, -0.5, "lin"],
                 [3500, -0.68, "inout"], [3830, 1, "inout"]]),
    breathe: Math.sin((2 * Math.PI * t) / 2400) * 0.014 * settled
      + Math.sin((2 * Math.PI * t) / 850) * 0.02 * fury,
  };
}

/* ---------- camera ----------
 * Resting band; the SNAP runs 1x -> 5x in 260ms with overshoot, landing
 * the monocled glare filling the frame; creeps to 5.3x through the hold
 * (drifting down as the head lowers); whips back out in 310ms for the snub. */
function camera(t: number) {
  const z = kf(t, [[1650, 1, "lin"], [1910, 5.0, "back"], [3550, 5.3, "lin"], [3860, 1, "out"]]);
  const cx = kf(t, [[1650, 160, "lin"], [1910, 166, "back"], [3550, 166.5, "lin"], [3860, 160, "out"]]);
  const cy = kf(t, [[1650, 74, "lin"], [1910, 81, "back"], [3550, 82.5, "lin"], [3860, 74, "out"]]);
  const w = BAND.w / z, h = BAND.h / z;
  let vx = cx - w / 2, vy = cy - h / 2;
  if (t > 1910 && t < 2210) {              // impact shake as the snap lands
    const amp = 1.3 * Math.exp(-(t - 1910) / 110);
    vx += amp * Math.sin(t / 21);
    vy += 0.6 * amp * Math.sin(t / 15 + 2);
  }
  return { x: vx, y: vy, w, h };
}

const sceneAlpha = (t: number) =>
  kf(t, [[0, 0], [300, 1, "out"], [5650, 1, "lin"], [6200, 0, "inout"]]);

/* ---------- whistled notes: off the beak on the saunter, gone by the snap */
const NOTE_AT = [350, 720, 1050];
const NOTE_DUR = 1000;
const NOTE_GLYPHS = ["♪", "♫", "♪"];

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

  const NS = "http://www.w3.org/2000/svg";
  const el = <T extends SVGElement>(n: string, at: Record<string, string>): T => {
    const e = document.createElementNS(NS, n) as T;
    for (const k in at) e.setAttribute(k, at[k]);
    return e;
  };

  /* scenery under the bird: contact shadows + THE Apple logo (parody — flat
   * Mac-aluminum silver, bitten on the right, detached leaf; hand-authored) */
  const scenery = el<SVGGElement>("g", {});
  const birdShadow = el<SVGEllipseElement>("ellipse", {
    cy: String(GROUND_Y + 2.6), rx: "15", ry: "2.1", fill: "#000", opacity: "0.17",
  });
  const appleShadow = el<SVGEllipseElement>("ellipse", {
    cx: String(APPLE_X), cy: String(GROUND_Y + 2.4), rx: "10", ry: "1.9",
    fill: "#000", opacity: "0.2",
  });
  const APPLE_S = 1.15;              // authored ~24 units tall -> ~26 scene units
  const apple = el<SVGGElement>("g", {
    transform: `translate(${(APPLE_X - 12 * APPLE_S).toFixed(1)} ` +
      `${(GROUND_Y + 1.5 - 22 * APPLE_S).toFixed(1)}) scale(${APPLE_S})`,
  });
  apple.innerHTML =
    /* leaf: a lens shape angled up-right, detached above the body */
    `<path fill="#bfc4cb" d="M14.2 5.4 C 14.1 3.9, 15.2 2.4, 16.9 2.1 ` +
    `C 17.1 3.7, 16.0 5.2, 14.2 5.4 Z"/>` +
    /* body: top dip, two-lobe bottom, one concave bite out of the right */
    `<path fill="#bfc4cb" d="M13.0 6.8 C 13.9 6.2, 15.3 5.9, 16.5 6.3 ` +
    `C 17.6 6.7, 18.5 7.6, 19.0 8.7 ` +
    `C 17.2 9.6, 16.3 11.1, 16.4 12.7 C 16.5 14.1, 17.4 15.3, 18.7 15.9 ` +
    `C 18.3 17.5, 17.5 19.2, 16.4 20.4 C 15.6 21.3, 14.6 22.0, 13.6 21.9 ` +
    `C 12.9 21.85, 12.6 21.4, 12.0 21.4 C 11.4 21.4, 11.1 21.85, 10.4 21.9 ` +
    `C 9.2 22.0, 8.2 21.2, 7.4 20.2 C 6.0 18.4, 5.0 16.0, 4.9 13.6 ` +
    `C 4.8 11.2, 5.6 8.9, 7.3 7.5 C 8.4 6.6, 9.9 6.2, 11.2 6.6 ` +
    `C 11.9 6.8, 12.4 7.0, 13.0 6.8 Z"/>`;
  scenery.append(birdShadow, appleShadow, apple);
  svg.insertBefore(scenery, svg.children[1]);   // above the hidden wire, below the bird

  /* the whistled notes float above everything, accent-colored */
  const noteEls = NOTE_GLYPHS.map((g, i) => {
    const n = el<SVGTextElement>("text", {
      "font-size": i === 1 ? "9.5" : "8",
      "font-family": `"Segoe UI Symbol", sans-serif`,
      fill: "var(--color-accent,#76B9ED)", "text-anchor": "middle",
    });
    n.textContent = g;
    n.style.visibility = "hidden";
    svg.appendChild(n);
    return n;
  });

  /* the monocle glint — two parallel streaks swept across the lens on the
   * snap (the monocle stays seated; only the light moves) */
  const glint = el<SVGGElement>("g", {});
  const glintAt = { stroke: "var(--ink,#e9eef4)", "stroke-linecap": "round", fill: "none" };
  glint.append(
    el<SVGPathElement>("path", { ...glintAt, "stroke-width": "1.0", d: "M156.6 80.2 L 161.0 76.0" }),
    el<SVGPathElement>("path", { ...glintAt, "stroke-width": "0.65", d: "M159.4 82.2 L 162.4 79.3" }),
  );
  glint.style.visibility = "hidden";
  svg.appendChild(glint);

  const setVB = (r: { x: number; y: number; w: number; h: number }) =>
    svg.setAttribute("viewBox",
      `${r.x.toFixed(2)} ${r.y.toFixed(2)} ${r.w.toFixed(2)} ${r.h.toFixed(2)}`);

  function drawFrame(t: number, still = false): void {
    const p = birdPose(t);
    scene.apply(p);
    birdShadow.setAttribute("cx", p.x.toFixed(2));
    birdShadow.setAttribute("rx", (15 + p.bob * 0.9).toFixed(2));

    for (let i = 0; i < noteEls.length; i++) {
      const ph = (t - NOTE_AT[i]) / NOTE_DUR;
      if (!still && ph > 0 && ph < 1) {
        /* off the beak, drifting up-right with a lazy sway — the taller
         * stage gives them sky to climb into */
        const nx = birdX(NOTE_AT[i]) + 40 + ph * 14 + Math.sin(ph * Math.PI * 2.4) * 2;
        const ny = GROUND_Y - 25 - 30 * EASE.out(ph);
        noteEls[i].style.visibility = "visible";
        noteEls[i].setAttribute("x", nx.toFixed(1));
        noteEls[i].setAttribute("y", ny.toFixed(1));
        noteEls[i].setAttribute("transform",
          `rotate(${(Math.sin(ph * Math.PI * 3) * 10).toFixed(1)} ${nx.toFixed(1)} ${ny.toFixed(1)})`);
        noteEls[i].style.opacity = (Math.sin(Math.PI * ph) * 0.9).toFixed(3);
      } else {
        noteEls[i].style.visibility = "hidden";
      }
    }

    if (!still && t > 1830 && t < 2370) {
      const u = (t - 1830) / 540;
      glint.style.visibility = "visible";
      glint.style.opacity =
        kf(t, [[1830, 0], [1970, 0.95, "out"], [2130, 0.95, "lin"], [2370, 0]]).toFixed(3);
      const s = (u * 3.4 - 1.2).toFixed(2);
      glint.setAttribute("transform", `translate(${s} ${s})`);
    } else {
      glint.style.visibility = "hidden";
    }

    setVB(still ? BAND : camera(t));
    wrap.style.opacity = still ? "1" : sceneAlpha(t).toFixed(3);
  }

  wrap.appendChild(svg);

  if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
    drawFrame(STILL_T, true);   // just clocked the apple; instantly unimpressed
    return wrap;
  }

  /* scrub hook: ?scene-t=2000 freezes the loop at that ms — every reviewed
   * frame reproducible by number, like the rig's design-loop mockups */
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
