// Bot faces — the team room's avatars. Monocle Guy, bust-cropped into the
// room's 28 px circle, told apart by what he wears: a HAT that means the bot's
// position (never random), an EYEWEAR and at most one EXTRA (neckwear, or a
// tool / wardrobe item) that the host picks at random and stores, and a
// TEMPERAMENT that shows in how he moves. Colour is a global mode: off (the
// default) is the plain ink mascot on a quiet neutral circle, exactly the
// setup overlay's bird; on, the bird and circle take the bot's tag colour.
//
// Ported from three reviewed mockups in design-loop/ (each can scrub and
// filmstrip any frame; keep them in sync when the acting changes):
//   team-avatar-hats.html      the six hats, the hat spring, the base loops
//                              (idle / working / waiting / asleep) and the
//                              300 ms cross-fade on a state switch
//   team-avatar-tools.html     headset, scarf, spanner, pencil, magnifier,
//                              spectacles; each item's working acting and
//                              its droop asleep
//   team-avatar-character.html monocle, rect, round, loupe, goggles, bow tie,
//                              crest; the six temperaments; blink phase
// The hats loops are the base; a temperament's beats and an item's acting
// layer on. Every state is a pure pose(t) loop that starts and ends on the
// bot's neutral perch, so states chain and cross-fade without a pop, and any
// frame is reproducible by number (poseAt is exported for the tests).
//
// The bird's geometry comes from setup-overlay.ts — same silhouette, never
// redrawn. Nothing here touches the DOM at import time.

import { MASCOT, browD, kf, blink, EASES, clamp01 } from "./setup-overlay.js";
import type { Key } from "./setup-overlay.js";

/* ================================================================
   Public vocabulary
   ================================================================ */
export type FaceHat = "captain" | "beanie" | "hardhat" | "beret" | "deerstalker" | "tophat";
export type FaceEyewear = "monocle" | "pincenez" | "round" | "rect" | "loupe" | "goggles" | "spectacles";
export type FaceExtra = "none" | "bowtie" | "tie" | "scarf" | "crest" | "headset" | "pencil" | "spanner" | "magnifier";
export type FaceTemper = "steady" | "quick" | "curious" | "wary" | "keen" | "lead";
export type FaceState = "idle" | "working" | "waiting" | "asleep";

export interface BotLook { hat: FaceHat; eyewear: FaceEyewear; extra: FaceExtra; temper: FaceTemper; }

export const FACE_HATS: readonly FaceHat[] = ["captain", "beanie", "hardhat", "beret", "deerstalker", "tophat"];
export const FACE_EYEWEAR: readonly FaceEyewear[] = ["monocle", "pincenez", "round", "rect", "loupe", "goggles", "spectacles"];
export const FACE_EXTRAS: readonly FaceExtra[] = ["none", "bowtie", "tie", "scarf", "crest", "headset", "pencil", "spanner", "magnifier"];
export const FACE_TEMPERS: readonly FaceTemper[] = ["steady", "quick", "curious", "wary", "keen", "lead"];
export const FACE_STATES: readonly FaceState[] = ["idle", "working", "waiting", "asleep"];

/** Loop length per state (ms). Every loop starts AND ends on the neutral perch. */
export const FACE_LOOP_MS: Readonly<Record<FaceState, number>> = { idle: 5200, working: 2800, waiting: 4600, asleep: 8000 };

const DEFAULT_LOOK: BotLook = { hat: "beanie", eyewear: "monocle", extra: "none", temper: "steady" };

/** Coerce whatever the host stored into a BotLook. Unknown or missing values
 *  fall back to the defaults; never throws. */
export function normalizeLook(raw: Partial<Record<keyof BotLook, string>> | null | undefined): BotLook {
  const pick = <T extends string>(v: unknown, ok: readonly T[], d: T): T => {
    if (typeof v !== "string") return d;
    const s = v.trim().toLowerCase();
    return (ok as readonly string[]).includes(s) ? (s as T) : d;
  };
  const r: Partial<Record<keyof BotLook, unknown>> = raw && typeof raw === "object" ? raw : {};
  return {
    hat: pick(r.hat, FACE_HATS, DEFAULT_LOOK.hat),
    eyewear: pick(r.eyewear, FACE_EYEWEAR, DEFAULT_LOOK.eyewear),
    extra: pick(r.extra, FACE_EXTRAS, DEFAULT_LOOK.extra),
    temper: pick(r.temper, FACE_TEMPERS, DEFAULT_LOOK.temper),
  };
}

/* ================================================================
   Timeline helpers (kf / blink / EASES come from the rig)
   ================================================================ */
const { K, FEET, BODY_D, BEAK_D, EYE_INK } = MASCOT;
const sin = Math.sin, PI = Math.PI;
const clamp = (v: number, a: number, b: number) => Math.min(b, Math.max(a, v));
const blinks = (t: number, ...ats: number[]) => ats.reduce((m, a) => Math.min(m, blink(t, a)), 1);
/** trapezoid envelope: ramps in over r ms after a, out over r ms before b */
const env = (t: number, a: number, b: number, r = 100) => clamp01((t - a) / r) * clamp01((b - t) / r);
/** a phase channel is only alive inside (0,1); outside it is simply off */
const unit = (u: number) => (u > 0 && u < 1 ? u : 0);
const breath = (t: number, len: number, cycles: number, amp: number) => sin(2 * PI * cycles * t / len) * amp;

/* ================================================================
   Pose: every number the rig needs. Motion channels are in the rig's
   own units (the setup overlay's), item channels are read by whichever
   item is worn, the spring channels are the follow-through the rig
   derives from the head's recent motion.
   ================================================================ */
type Pose = {
  bob: number; crouch: number; bodyRot: number; headTilt: number; headDX: number; headDY: number;
  tailAng: number; blink: number; breathe: number; brow: number;
  eyeScale: number; gaze: number; beak: number;
  slip: number; pop: number; lensDY: number; lensRot: number; glint: number;
  noteVis: number; scribble: number; jitter: number; lineA: number; padFlip: number;
  z1: number; z2: number; z3: number; z4: number;
  itemA: number; itemB: number; itemC: number; droop: number;
  rA: number; rY: number; rX: number;   // stiff spring (the hat body)
  sA: number; sY: number; sX: number;   // soft spring (bobble, strap, flop)
  mA: number; mY: number;               // mid spring (tools, neckwear, chain)
};
type PoseKey = keyof Pose;

const CALM: Pose = {
  bob: 0, crouch: 0.55, bodyRot: 0, headTilt: 0, headDX: 0, headDY: 0,
  tailAng: 4, blink: 1, breathe: 0, brow: 0.25,
  eyeScale: 1, gaze: 0, beak: 0,
  slip: 0, pop: 0, lensDY: 0, lensRot: 0, glint: 0,
  noteVis: 0, scribble: 0, jitter: 0, lineA: 1, padFlip: 1,
  z1: 0, z2: 0, z3: 0, z4: 0,
  itemA: 0, itemB: 0, itemC: 0, droop: 0,
  rA: 0, rY: 0, rX: 0, sA: 0, sY: 0, sX: 0, mA: 0, mY: 0,
};

/* An overlay adds to the motion channels, multiplies the eye, replaces the
   rest — the tools mockup's merge(), widened for the temperament beats. */
const ADD = new Set<PoseKey>(["bob", "bodyRot", "headTilt", "headDX", "headDY", "crouch", "brow", "gaze", "lensDY", "lensRot", "slip"]);
const MUL = new Set<PoseKey>(["blink", "eyeScale", "breathe"]);
function merge(p: Pose, o: Partial<Pose>): Pose {
  for (const k in o) {
    const key = k as PoseKey, v = o[key]!;
    if (ADD.has(key)) p[key] += v;
    else if (MUL.has(key)) p[key] *= v;
    else p[key] = v;
  }
  return p;
}

/* ================================================================
   Temperaments — variant C's six. The resting brow is the attitude at
   the neutral perch; tempo scales the peck rate while working; mid /
   wait / sleep name the temperament's own beat inside the shared
   working / waiting / asleep loops; glintAt is where the idle loop's
   head comes home, so the monocle catches the light there.
   ================================================================ */
type MidBeat = "scan" | "dart" | "nod" | "squint" | "tally" | "doubletake";
type WaitBeat = "hop" | "hophop" | "nod" | "tilt" | "lensup" | "pop";
type SleepBeat = "still" | "twitch" | "snore" | "lean" | "droop" | "jolt";
interface Temper {
  brow: number; tempo: number; mid: MidBeat; wait: WaitBeat; sleep: SleepBeat; glintAt: number;
  idle: (i: number, tp: Temper, ph: number) => Pose;
}
const neutral = (tp: Temper): Pose => ({ ...CALM, brow: tp.brow });

const IDLE_MS = FACE_LOOP_MS.idle, WORK_MS = FACE_LOOP_MS.working, WAIT_MS = FACE_LOOP_MS.waiting, SLEEP_MS = FACE_LOOP_MS.asleep;

/* ---- idle, 5.2 s: where the temperament lives most of all ---- */

/* the lead scans the room: over the shoulder, then forward-down across the
   table; an impatient foot tap (legs are cropped, so it reads as a rhythmic
   bob the head rides); one glance back before settling */
function idleLead(i: number, tp: Temper, ph: number): Pose {
  const tap = env(i, 2700, 3900, 150);
  const tapW = Math.abs(sin(2 * PI * (i - 2700) / 380));
  return { ...neutral(tp),
    breathe: breath(i, IDLE_MS, 2, .012),
    headTilt: kf(i, [[0, 0], [420, -10, "back"], [1200, -10, "lin"], [1650, 8, "inout"], [2350, 8, "lin"], [2750, 0, "inout"],
                     [4300, 0, "lin"], [4520, -5, "out"], [4950, 0, "inout"]]),
    headDX: kf(i, [[0, 0], [420, -1.5, "back"], [1200, -1.5, "lin"], [1650, 1.6, "inout"], [2350, 1.6, "lin"], [2750, 0, "inout"],
                   [4300, 0, "lin"], [4520, -.6, "out"], [4950, 0, "inout"]]),
    headDY: kf(i, [[0, 0], [420, -.6, "back"], [1200, -.6, "lin"], [1650, .7, "inout"], [2350, .7, "lin"], [2750, 0, "inout"]]),
    bodyRot: kf(i, [[1200, 0], [1650, 2.5, "inout"], [2350, 2.5, "lin"], [2750, 0, "inout"]]),
    bob: -tapW * .8 * tap,
    tailAng: kf(i, [[3900, 4], [4060, 13, "back"], [4400, 4, "out"]]),
    blink: blinks(i, 900 + ph, 3050 + ph, 3170 + ph, 4700 + ph),
    brow: kf(i, [[0, tp.brow], [420, .8, "back"], [1200, .8, "lin"], [1650, .1, "inout"], [2350, .1, "lin"], [2750, tp.brow, "inout"],
                 [2900, -.1, "inout"], [3900, -.1, "lin"], [4200, tp.brow, "inout"], [4520, .7, "out"], [4950, tp.brow, "inout"]]),
  };
}

/* quick cannot sit still: two clusters of darts, a body shift each way, two
   tail flicks, three breaths per loop, double blinks */
function idleQuick(i: number, tp: Temper, ph: number): Pose {
  return { ...neutral(tp),
    breathe: breath(i, IDLE_MS, 3, .012),
    headTilt: kf(i, [[0, 0], [240, -8, "back"], [560, -8, "lin"], [700, 6, "out"], [1000, 6, "lin"], [1200, 0, "inout"],
                     [2500, 0, "lin"], [2620, -6, "out"], [2820, -6, "lin"], [2940, 7, "out"], [3220, 7, "lin"], [3480, 0, "inout"],
                     [4200, 0, "lin"], [4310, -4, "out"], [4560, 0, "out"]]),
    headDX: kf(i, [[0, 0], [240, -1.3, "back"], [560, -1.3, "lin"], [700, 1.3, "out"], [1000, 1.3, "lin"], [1200, 0, "inout"],
                   [2500, 0, "lin"], [2620, -1, "out"], [2820, -1, "lin"], [2940, 1.5, "out"], [3220, 1.5, "lin"], [3480, 0, "inout"],
                   [4200, 0, "lin"], [4310, -.6, "out"], [4560, 0, "out"]]),
    headDY: kf(i, [[0, 0], [240, -.6, "back"], [560, -.6, "lin"], [700, .5, "out"], [1000, .5, "lin"], [1200, 0, "inout"],
                   [2620, 0, "lin"], [2940, .6, "out"], [3220, .6, "lin"], [3480, 0, "inout"]]),
    bodyRot: kf(i, [[1400, 0], [1550, 2, "out"], [1900, 2, "lin"], [2100, 0, "inout"], [3600, 0, "lin"], [3720, -1.5, "out"], [4000, 0, "inout"]]),
    tailAng: kf(i, [[1500, 4], [1640, 14, "back"], [1950, 4, "out"], [3700, 4, "lin"], [3840, 13, "back"], [4150, 4, "out"]]),
    blink: blinks(i, 450 + ph, 1650 + ph, 1740 + ph, 3080 + ph, 4380 + ph),
    brow: kf(i, [[0, tp.brow], [240, .9, "back"], [560, .9, "lin"], [700, .3, "out"], [1000, .3, "lin"], [1200, tp.brow, "inout"],
                 [2620, .85, "out"], [2820, .85, "lin"], [2940, .2, "out"], [3220, .2, "lin"], [3480, tp.brow, "inout"],
                 [4310, .75, "out"], [4560, tp.brow, "out"]]),
  };
}

/* steady is unflappable: two slow, deliberate nods, a level brow, breathing
   a touch deeper, blinks evenly spaced on purpose */
function idleSteady(i: number, tp: Temper, ph: number): Pose {
  return { ...neutral(tp),
    breathe: breath(i, IDLE_MS, 2, .014),
    headTilt: kf(i, [[900, 0], [1500, 7, "inout"], [1950, 7, "lin"], [2550, 0, "inout"], [3100, 0, "lin"], [3650, 5, "inout"], [3950, 5, "lin"], [4500, 0, "inout"]]),
    headDX: kf(i, [[900, 0], [1500, .9, "inout"], [1950, .9, "lin"], [2550, 0, "inout"], [3100, 0, "lin"], [3650, .6, "inout"], [3950, .6, "lin"], [4500, 0, "inout"]]),
    headDY: kf(i, [[900, 0], [1500, .5, "inout"], [1950, .5, "lin"], [2550, 0, "inout"], [3100, 0, "lin"], [3650, .35, "inout"], [3950, .35, "lin"], [4500, 0, "inout"]]),
    bodyRot: kf(i, [[900, 0], [1500, 1.5, "inout"], [1950, 1.5, "lin"], [2550, 0, "inout"]]),
    tailAng: kf(i, [[0, 4], [2600, 3, "inout"], [4500, 4, "inout"]]),
    blink: blinks(i, 1300 + ph, 3900 + ph),
    brow: kf(i, [[0, tp.brow], [1500, -.12, "inout"], [2550, tp.brow, "inout"], [3650, -.05, "inout"], [4500, tp.brow, "inout"]]),
  };
}

/* curious tilts to one side, squints at it, tilts to the other, squints
   again, then decides: a raised-brow "yes" and a tail flick */
function idleCurious(i: number, tp: Temper, ph: number): Pose {
  const squint = kf(i, [[900, 1], [1150, .5, "inout"], [1550, .5, "lin"], [1800, 1, "out"], [2500, 1, "lin"], [2750, .45, "inout"], [3250, .45, "lin"], [3500, 1, "out"]]);
  return { ...neutral(tp),
    breathe: breath(i, IDLE_MS, 2, .012),
    headTilt: kf(i, [[0, 0], [450, -16, "back"], [1600, -16, "lin"], [2100, 12, "inout"], [3300, 12, "lin"], [3800, 0, "inout"], [4000, -5, "out"], [4600, 0, "inout"]]),
    headDX: kf(i, [[0, 0], [450, -.9, "back"], [1600, -.9, "lin"], [2100, 1.2, "inout"], [3300, 1.2, "lin"], [3800, 0, "inout"], [4000, -.4, "out"], [4600, 0, "inout"]]),
    headDY: kf(i, [[0, 0], [450, -.7, "back"], [1600, -.7, "lin"], [2100, .8, "inout"], [3300, .8, "lin"], [3800, 0, "inout"], [4000, -.4, "out"], [4600, 0, "inout"]]),
    bodyRot: kf(i, [[1600, 0], [2100, 3, "inout"], [3300, 3, "lin"], [3800, 0, "inout"]]),
    tailAng: kf(i, [[3950, 4], [4110, 14, "back"], [4450, 4, "out"]]),
    blink: Math.min(squint, blinks(i, 4350 + ph)),
    brow: kf(i, [[0, tp.brow], [450, .6, "back"], [900, .6, "lin"], [1150, -.35, "inout"], [1550, -.35, "lin"], [1800, tp.brow, "out"], [2500, tp.brow, "lin"],
                 [2750, -.4, "inout"], [3250, -.4, "lin"], [3800, tp.brow, "inout"], [4000, .95, "back"], [4300, .95, "lin"], [4700, tp.brow, "inout"]]),
  };
}

/* keen leans in and peers, tallies (three counting nods), then sits back
   with a raised "hm" and lets it settle */
function idleKeen(i: number, tp: Temper, ph: number): Pose {
  const nods = kf(i, [[1300, 0], [1400, 3, "out"], [1560, 0, "out"], [1700, 3, "out"], [1860, 0, "out"], [2000, 3, "out"], [2160, 0, "out"]]);
  return { ...neutral(tp),
    breathe: breath(i, IDLE_MS, 2, .012),
    headTilt: kf(i, [[0, 0], [500, 12, "inout"], [2700, 12, "lin"], [3100, -6, "back"], [3800, -6, "lin"], [4300, 0, "inout"]]) + nods,
    headDX: kf(i, [[0, 0], [500, 1.8, "inout"], [2700, 1.8, "lin"], [3100, -.7, "back"], [3800, -.7, "lin"], [4300, 0, "inout"]]),
    headDY: kf(i, [[0, 0], [500, 1.1, "inout"], [2700, 1.1, "lin"], [3100, -.6, "back"], [3800, -.6, "lin"], [4300, 0, "inout"]]) + nods * .12,
    bodyRot: kf(i, [[0, 0], [500, 3.5, "inout"], [2700, 3.5, "lin"], [3100, -1, "back"], [3800, -1, "lin"], [4300, 0, "inout"]]),
    tailAng: kf(i, [[3100, 4], [3260, 13, "back"], [3600, 4, "out"]]),
    blink: blinks(i, 2550 + ph, 3950 + ph, 4040 + ph),
    brow: kf(i, [[0, tp.brow], [500, -.25, "inout"], [2700, -.25, "lin"], [3100, .9, "back"], [3800, .9, "lin"], [4300, tp.brow, "inout"]]),
  };
}

/* wary: glance, snap back casual, WHIP back wide-eyed and freeze (the
   double-take), a slow suspicious settle, then a flat side-eye */
function idleWary(i: number, tp: Temper, ph: number): Pose {
  const wide = kf(i, [[1450, 1], [1560, 1.15, "out"], [2150, 1.15, "lin"], [2650, 1, "inout"]]);
  return { ...neutral(tp),
    breathe: breath(i, IDLE_MS, 2, .012),
    headTilt: kf(i, [[900, 0], [1040, 10, "out"], [1180, 10, "lin"], [1310, -3, "out"], [1450, -3, "lin"], [1560, 14, "out"], [2150, 14, "lin"],
                     [2900, 0, "inout"], [3600, 0, "lin"], [3900, -6, "inout"], [4500, -6, "lin"], [5000, 0, "inout"]]),
    headDX: kf(i, [[900, 0], [1040, 1.3, "out"], [1180, 1.3, "lin"], [1310, -.5, "out"], [1450, -.5, "lin"], [1560, 1.9, "out"], [2150, 1.9, "lin"],
                   [2900, 0, "inout"], [3600, 0, "lin"], [3900, -.8, "inout"], [4500, -.8, "lin"], [5000, 0, "inout"]]),
    headDY: kf(i, [[900, 0], [1040, .7, "out"], [1180, .7, "lin"], [1310, -.3, "out"], [1450, -.3, "lin"], [1560, 1, "out"], [2150, 1, "lin"], [2900, 0, "inout"]]),
    bodyRot: kf(i, [[1450, 0], [1560, 3, "out"], [2150, 3, "lin"], [2900, 0, "inout"]]),
    tailAng: kf(i, [[1560, 4], [1700, 14, "back"], [2100, 4, "out"]]),
    blink: blinks(i, 420 + ph, 3000 + ph, 3080 + ph, 4700 + ph),
    eyeScale: wide,
    brow: kf(i, [[0, tp.brow], [900, tp.brow], [1040, .15, "out"], [1180, .15, "lin"], [1310, .7, "out"], [1450, .7, "lin"], [1560, 1.05, "out"], [2150, 1.05, "lin"],
                 [2900, -.2, "inout"], [3600, -.2, "lin"], [3900, -.4, "inout"], [4500, -.4, "lin"], [5000, tp.brow, "inout"]]),
  };
}

const TEMPERS: Record<FaceTemper, Temper> = {
  lead:    { brow: .35, tempo: 1.0,  mid: "scan",       wait: "hop",    sleep: "still",  glintAt: 2800, idle: idleLead },
  quick:   { brow: .45, tempo: 1.45, mid: "dart",       wait: "hophop", sleep: "twitch", glintAt: 1250, idle: idleQuick },
  steady:  { brow: .10, tempo: .70,  mid: "nod",        wait: "nod",    sleep: "snore",  glintAt: 2600, idle: idleSteady },
  curious: { brow: .30, tempo: .95,  mid: "squint",     wait: "tilt",   sleep: "lean",   glintAt: 3850, idle: idleCurious },
  keen:    { brow: .20, tempo: .85,  mid: "tally",      wait: "lensup", sleep: "droop",  glintAt: 4350, idle: idleKeen },
  wary:    { brow: .50, tempo: 1.15, mid: "doubletake", wait: "pop",    sleep: "jolt",   glintAt: 2950, idle: idleWary },
};

/* ---- working, 2.8 s (hats): head down over a pad, two scribble bursts
   (lines appear as he writes), the temperament's own beat between them
   in the 1000–1600 window, a satisfied lift while the page flips, back
   down. The mid beats are variant C's, moved into this window. ---- */
interface Mid { tilt: Key[]; dx: Key[]; dy: Key[]; brow: Key[]; blink?: Key[]; wide?: Key[] }
const MID: Record<MidBeat, Mid> = {
  scan:       { tilt: [[1150, -5, "back"], [1450, -5, "lin"]], dx: [[1150, -1, "back"], [1450, -1, "lin"]],
                dy: [[1150, -.6, "back"], [1450, -.6, "lin"]], brow: [[1150, .75, "back"], [1450, .75, "lin"]] },
  dart:       { tilt: [[1080, -6, "out"], [1200, -6, "lin"], [1310, 15, "out"], [1450, 15, "lin"]],
                dx: [[1080, -1.1, "out"], [1200, -1.1, "lin"], [1310, 2, "out"], [1450, 2, "lin"]],
                dy: [[1080, -.5, "out"], [1200, -.5, "lin"], [1310, 1.6, "out"], [1450, 1.6, "lin"]],
                brow: [[1080, .9, "out"], [1200, .9, "lin"], [1310, -.2, "out"], [1450, -.2, "lin"]] },
  nod:        { tilt: [[1250, 20, "inout"], [1400, 20, "lin"]], dx: [[1250, 2.4, "inout"], [1400, 2.4, "lin"]],
                dy: [[1250, 2.0, "inout"], [1400, 2.0, "lin"]], brow: [[1250, -.3, "inout"], [1400, -.3, "lin"]] },
  squint:     { tilt: [[1150, 8, "inout"], [1450, 8, "lin"]], dx: [[1150, 1, "inout"], [1450, 1, "lin"]], dy: [],
                brow: [[1150, -.4, "inout"], [1450, -.4, "lin"]],
                blink: [[1000, 1], [1150, .5, "inout"], [1450, .5, "lin"], [1600, 1, "out"]] },
  tally:      { tilt: [[1080, 18, "out"], [1200, 15, "out"], [1310, 18, "out"], [1430, 15, "out"], [1540, 18, "out"]],
                dx: [[1080, 2, "lin"]], dy: [], brow: [[1080, -.3, "lin"]] },
  doubletake: { tilt: [[1090, -1, "out"], [1220, -1, "lin"], [1320, 19, "out"], [1550, 19, "lin"]],
                dx: [[1090, -.6, "out"], [1220, -.6, "lin"], [1320, 2.4, "out"], [1550, 2.4, "lin"]],
                dy: [[1090, -.3, "out"], [1220, -.3, "lin"], [1320, 1.9, "out"], [1550, 1.9, "lin"]],
                brow: [[1090, .7, "out"], [1220, .7, "lin"], [1320, 1.05, "out"], [1550, 1.05, "lin"]],
                wide: [[1220, 1], [1320, 1.15, "out"], [1550, 1.15, "lin"], [1700, 1, "inout"]] },
};

function working(n: number, tp: Temper, ph: number, pad: boolean, peck: boolean): Pose {
  const per = 115 / tp.tempo;                       // peck period at this temperament
  const e = peck ? env(n, 250, 1000) + env(n, 1600, 2300) : 0;
  const jitter = e * sin(2 * PI * n / per);
  const m = MID[tp.mid];
  return { ...neutral(tp),
    breathe: breath(n, WORK_MS, 1, .012),
    noteVis: pad ? 1 : 0,
    scribble: pad ? kf(n, [[250, 0], [1000, .5, "lin"], [1600, .5], [2300, 1, "lin"]]) : 0,
    lineA: pad ? (n < 2350 ? 1 : kf(n, [[2350, 1], [2470, 0, "out"]])) : 1,
    padFlip: kf(n, [[2350, 1], [2440, .72, "out"], [2560, 1.06, "out"], [2700, 1, "inout"]]),
    jitter,
    headTilt: kf(n, [[0, 0], [250, 15, "inout"], [1000, 15, "lin"], ...m.tilt,
                     [1600, 15, "inout"], [2300, 15, "lin"], [2520, -3, "back"], [2800, 0, "inout"]]) + jitter * 1.6,
    headDX: kf(n, [[0, 0], [250, 2.2, "inout"], [1000, 2.2, "lin"], ...m.dx,
                   [1600, 2.2, "inout"], [2300, 2.2, "lin"], [2520, -.2, "back"], [2800, 0, "inout"]]),
    headDY: kf(n, [[0, 0], [250, 1.6, "inout"], [1000, 1.6, "lin"], ...m.dy,
                   [1600, 1.6, "inout"], [2300, 1.6, "lin"], [2520, -.3, "back"], [2800, 0, "inout"]]) + jitter * .35,
    bodyRot: kf(n, [[0, 0], [250, 3, "inout"], [2300, 3, "lin"], [2800, 0, "inout"]]),
    tailAng: kf(n, [[2300, 4], [2440, 14, "back"], [2750, 4, "out"]]),
    blink: Math.min(blink(n, 1300 + ph), blink(n, 2450 - ph)) * (m.blink ? kf(n, m.blink) : 1),
    eyeScale: m.wide ? kf(n, m.wide) : 1,
    brow: kf(n, [[0, tp.brow], [250, -.2, "inout"], [1000, -.2, "lin"], ...m.brow,
                 [1600, -.2, "inout"], [2300, -.2, "lin"], [2520, .7, "back"], [2800, tp.brow, "inout"]]),
  };
}

/* ---- waiting, 4.6 s (hats): brow up, a lean-in at the viewer with the eye
   a touch wider; one attention beat at 1800 — the temperament's own — then
   he eases off to neutral and leans in again on loop ---- */
function waiting(w: number, tp: Temper, ph: number, eyewear: FaceEyewear): Pose {
  const p: Pose = { ...neutral(tp),
    breathe: breath(w, WAIT_MS, 2, .010),
    bodyRot: kf(w, [[0, 0], [300, 5, "back"], [4000, 5, "lin"], [4600, 0, "inout"]]),
    headTilt: kf(w, [[0, 0], [300, -11, "back"], [4000, -11, "lin"], [4600, 0, "inout"]]),
    headDX: kf(w, [[0, 0], [300, 2.2, "back"], [4000, 2.2, "lin"], [4600, 0, "inout"]]),
    headDY: kf(w, [[0, 0], [300, -1.2, "back"], [4000, -1.2, "lin"], [4600, 0, "inout"]]),
    eyeScale: kf(w, [[0, 1], [300, 1.2, "back"], [4000, 1.2, "lin"], [4600, 1, "inout"]]),
    brow: kf(w, [[0, tp.brow], [300, 1.05, "back"], [4000, 1.05, "lin"], [4600, tp.brow, "inout"]]),
    blink: Math.min(blink(w, 1200 + ph), blink(w, 3400 - ph)),
  };
  switch (tp.wait) {
    case "hop":      // anticipation squat, one hop, the landing pops the monocle, settle
      p.headTilt += kf(w, [[1900, 0], [1990, 5, "inout"], [2080, -4, "out"], [2250, 0, "out"]]);
      p.eyeScale *= kf(w, [[2140, 1], [2220, 1.1, "back"], [2600, 1, "inout"]]);
      p.brow += kf(w, [[2140, 0], [2220, .25, "back"], [2600, 0, "inout"]]);
      p.crouch = kf(w, [[1800, .55], [1930, .95, "out"], [2000, .1, "out"], [2140, .9, "out"], [2330, .55, "inout"]]);
      p.bob = kf(w, [[1930, 0], [2030, -2.6, "out"], [2140, 0, "lin"]]);
      p.tailAng = kf(w, [[1930, 4], [2040, 15, "back"], [2400, 4, "out"]]);
      p.pop = kf(w, [[2140, 0], [2240, 1, "back"], [2380, 1, "lin"], [2700, 0, "inout"]]);
      break;
    case "hophop":   // two quick hops, the second smaller; the monocle pops on the last landing
      p.crouch += kf(w, [[1800, 0], [1900, .3, "out"], [1960, -.2, "out"], [2100, .25, "out"], [2160, -.1, "out"], [2280, .2, "out"], [2450, 0, "inout"]]);
      p.bob = kf(w, [[1900, 0], [2000, -2, "out"], [2100, 0, "lin"], [2160, 0, "lin"], [2230, -1.2, "out"], [2300, 0, "lin"]]);
      p.tailAng = kf(w, [[1900, 4], [2000, 15, "back"], [2400, 4, "out"]]);
      p.eyeScale *= kf(w, [[2300, 1], [2380, 1.1, "back"], [2700, 1, "inout"]]);
      p.pop = kf(w, [[2300, 0], [2400, 1, "back"], [2540, 1, "lin"], [2860, 0, "inout"]]);
      break;
    case "nod":      // one slow, sure nod: whenever you are ready
      p.headTilt += kf(w, [[1800, 0], [2200, 8, "inout"], [2400, 8, "lin"], [2900, 0, "inout"]]);
      p.headDY += kf(w, [[1800, 0], [2200, .8, "inout"], [2400, .8, "lin"], [2900, 0, "inout"]]);
      break;
    case "tilt":     // the head cocks over, the eyewear rides up a touch
      p.headTilt += kf(w, [[1800, 0], [2050, -13, "back"], [2650, -13, "lin"], [3050, 0, "inout"]]);
      p.headDX += kf(w, [[1800, 0], [2050, -.6, "back"], [2650, -.6, "lin"], [3050, 0, "inout"]]);
      p.lensDY = kf(w, [[1800, 0], [1950, -.35, "out"], [2650, -.35, "lin"], [3050, 0, "inout"]]);
      break;
    case "lensup":   // the loupe flips up off the eye (a monocle's chain swings); a lean-in peer for the rest
      p.lensRot = kf(w, [[1800, 0], [1980, -55, "back"], [2700, -55, "lin"], [2900, 0, "back"]]);
      p.headTilt += kf(w, [[1800, 0], [1980, -4, "out"], [2700, -4, "lin"], [2900, 0, "inout"]]);
      if (eyewear !== "loupe") {
        p.headDX += kf(w, [[1800, 0], [1980, .9, "out"], [2700, .9, "lin"], [2900, 0, "inout"]]);
        p.eyeScale *= kf(w, [[1800, 1], [1980, 1.08, "out"], [2700, 1.08, "lin"], [2900, 1, "inout"]]);
      }
      break;
    case "pop":      // the eyewear pops up and settles with a wobble, eyes wide
      p.lensDY = kf(w, [[1800, 0], [1900, -1.3, "out"], [2030, -.55, "out"], [2150, -.85, "out"], [2300, 0, "back"]]);
      p.headTilt += kf(w, [[1800, 0], [1900, -3, "out"], [2100, 0, "out"]]);
      p.eyeScale *= kf(w, [[1850, 1], [1920, 1.15, "out"], [2150, 1.15, "lin"], [2300, 1, "inout"]]);
      break;
  }
  return p;
}

/* ---- asleep, 8 s (hats): lids sink fast, head and body sag, the eyewear
   slips, two pairs of z's drift; a slow nod-off tips too far and he catches
   himself — wide-eyed for a blink, eyewear snapped back — and drifts off
   again on loop. Items droop with him; the temperament's sleep beat rides
   the doze. ---- */
function asleep(s: number, tp: Temper, ph: number): Pose {
  const e = env(s, 1400, 6300, 500);
  const sway = sin(2 * PI * (s - 1400) / 1100) * .05 * e;
  const lids = kf(s, [[0, 1], [300, .5, "out"], [1400, .32, "inout"], [5600, .28, "lin"], [6300, .12, "out"],
                      [6650, .12, "lin"], [6800, 1.05, "back"], [7300, 1, "lin"]]);
  const deep = tp.sleep === "snore" ? .034 : .024;
  const p: Pose = { ...neutral(tp),
    breathe: breath(s, SLEEP_MS, 3, 1) * kf(s, [[0, .012], [1400, deep, "inout"], [6300, deep, "lin"], [6800, .012, "out"]]),
    headTilt: kf(s, [[0, 0], [300, 5, "out"], [1400, 9, "inout"], [5600, 12, "lin"], [6300, 18, "out"],
                     [6650, 18, "lin"], [6800, -4, "back"], [7300, -4, "lin"], [8000, 0, "inout"]]),
    headDY: kf(s, [[0, 0], [300, .8, "out"], [1400, 1.6, "inout"], [5600, 2, "lin"], [6300, 2.4, "out"],
                   [6650, 2.4, "lin"], [6800, -.6, "back"], [7300, -.6, "lin"], [8000, 0, "inout"]]),
    headDX: kf(s, [[0, 0], [300, .3, "out"], [1400, .6, "inout"], [6300, 1, "out"],
                   [6650, 1, "lin"], [6800, -.3, "back"], [7300, -.3, "lin"], [8000, 0, "inout"]]),
    bodyRot: kf(s, [[0, 0], [300, 1.2, "out"], [1400, 2.5, "inout"], [6300, 4, "out"],
                    [6650, 4, "lin"], [6800, -1, "back"], [7300, -1, "lin"], [8000, 0, "inout"]]),
    crouch: .55 + kf(s, [[0, 0], [300, .1, "out"], [1400, .25, "inout"], [6650, .25, "lin"],
                         [6800, -.08, "back"], [7300, -.08, "lin"], [8000, 0, "inout"]]),
    tailAng: kf(s, [[0, 4], [1400, 1, "inout"], [6650, 1, "lin"], [6800, 14, "back"], [7200, 4, "out"]]),
    blink: Math.min(lids + sway, blink(s, 7550 + ph * .5)),
    slip: kf(s, [[0, 0], [300, .2, "out"], [1500, .6, "inout"], [6300, 1, "lin"], [6650, 1, "lin"], [6850, 0, "back"]]),
    droop: kf(s, [[0, 0], [300, .2, "out"], [1400, 1, "inout"], [6650, 1, "lin"], [6850, 0, "back"]]),
    z1: (s - 1600) / 1500, z2: (s - 2500) / 1400, z3: (s - 4000) / 1500, z4: (s - 4900) / 1400,
    brow: kf(s, [[0, tp.brow], [300, 0, "out"], [1400, -.3, "inout"], [6300, -.45, "lin"], [6650, -.45, "lin"],
                 [6800, 1, "back"], [7300, 1, "lin"], [8000, tp.brow, "inout"]]),
  };
  switch (tp.sleep) {
    case "twitch": {   // little jerks in the doze
      const tw = kf(s, [[3000, 0], [3080, -3, "out"], [3300, 0, "out"], [4600, 0, "lin"], [4670, -2.5, "out"], [4900, 0, "out"]]);
      p.headTilt += tw; p.headDX += tw * .12; break; }
    case "lean":       // sags sideways more than forward: head on a shoulder
      p.headDX += kf(s, [[0, 0], [1400, -.6, "inout"], [6650, -.9, "lin"], [6800, 0, "back"]]);
      p.headTilt += kf(s, [[0, 0], [1400, -4, "inout"], [6650, -5, "lin"], [6800, 0, "back"]]); break;
    case "droop":      // the loupe droops off its hinge (a monocle's chain hangs) as he dozes
      p.lensRot = kf(s, [[0, 0], [1500, 10, "inout"], [6300, 22, "lin"], [6650, 22, "lin"], [6850, 0, "back"]]); break;
    case "jolt": {     // a half-startle mid-doze: checks, finds nothing, re-sinks
      const j = kf(s, [[3700, 0], [3800, 1, "out"], [4000, 1, "lin"], [4550, 0, "inout"]]);
      p.blink = Math.max(p.blink, .6 * j); p.headTilt -= 5 * j; p.brow += .5 * j; p.slip -= .3 * j; break; }
    default: break;    // still, snore (deeper breathing, above)
  }
  return p;
}

/* ================================================================
   Item acting — the tools mockup's per-item overlays. Its working loop
   was 2.6 s; the hats' is 2.8 s, so its clock is scaled to land the
   bursts on the pad's. `pad`: keep the note pad. `peck`: the beak writes
   (the head jiggles with it) — off when the beak holds a tool.
   ================================================================ */
type Act = (τ: number, i: number) => Partial<Pose>;   // τ: the item's own clock; i: loop time
interface ExtraDef { pad: boolean; peck: boolean; idle?: Act; working?: Act; waiting?: Act }
const TW = 2600 / 2800;
const EXTRA_ACT: Partial<Record<FaceExtra, ExtraDef>> = {
  /* on the call: two talk bursts (the beak), an "uh-huh" nod pair, a glance aside that swings the mic */
  headset: { pad: false, peck: false,
    working: (τ) => {
      const e = env(τ, 120, 900, 80) + env(τ, 1500, 2300, 80);
      return { beak: e * (.5 - .5 * Math.cos(2 * PI * τ / 150)) * 9, brow: .7,
        headTilt: kf(τ, [[950, 0], [1060, 4, "back"], [1180, 0, "out"], [1230, 0], [1340, 4, "back"], [1460, 0, "out"]]),
        headDX: kf(τ, [[2300, 0], [2420, -.9, "back"], [2560, 0, "inout"]]) };
    } },
  /* the scarf tails flutter; typing bursts (a quick bob) with a glance at the screen between */
  scarf: { pad: true, peck: true,
    idle: (_τ, i) => ({ itemA: sin(2 * PI * 2 * i / IDLE_MS) * 2.5 }),
    waiting: (_τ, i) => ({ itemA: sin(2 * PI * 2 * i / WAIT_MS) * 2 }),
    working: (τ) => {
      const e = env(τ, 100, 1300) + env(τ, 1550, 2450);
      return { itemA: sin(2 * PI * 7 * τ / 2600) * 9, bob: -Math.abs(sin(2 * PI * 11 * τ / 2600)) * .6 * e,
        headTilt: kf(τ, [[1300, 0], [1420, -5, "back"], [1550, 0, "inout"]]),
        brow: kf(τ, [[1300, 0], [1420, .9, "back"], [1550, 0, "inout"]]) };
    } },
  /* tightening: anticipate, torque, overshoot, reset — twice */
  spanner: { pad: false, peck: false,
    working: (τ) => {
      const tq = kf(τ, [[130, 0], [430, 5, "back"], [800, 5], [1000, 0, "inout"], [1380, 0], [1680, 5, "back"], [2050, 5], [2250, 0, "inout"]]);
      return { itemA: kf(τ, [[0, 0], [130, -9, "out"], [430, 60, "back"], [800, 60, "lin"], [1000, 0, "inout"],
                             [1250, 0, "lin"], [1380, -9, "out"], [1680, 60, "back"], [2050, 60, "lin"], [2250, 0, "inout"]]),
        headTilt: tq, crouch: tq * .04, brow: -.2 };
    } },
  /* the pencil comes out to the beak and draws on the pad; the head follows the hand */
  pencil: { pad: true, peck: false,
    working: (τ) => {
      const e = env(τ, 150, 950) + env(τ, 1350, 2150);
      const jit = e * sin(2 * PI * τ / 110);
      return { itemA: 1,
        itemB: kf(τ, [[150, -1], [950, 1, "lin"], [1150, 1], [1350, -1, "inout"], [2150, 1, "lin"], [2300, 1], [2500, -1, "inout"]]),
        itemC: kf(τ, [[1150, 0], [1350, 1, "inout"], [2300, 1], [2500, 0, "inout"]]),
        headTilt: jit * 1.6 + kf(τ, [[950, 0], [1150, -6, "back"], [1300, -6], [1450, 0, "inout"]]),
        headDY: jit * .35,
        brow: kf(τ, [[950, 0], [1150, 1, "back"], [1300, 1], [1450, 0, "inout"], [2150, 0], [2350, .7, "back"], [2600, 0, "inout"]]) };
    } },
  /* the lens comes up over the eye, then sweeps the chest; a spotted-something brow */
  magnifier: { pad: false, peck: false,
    working: (τ) => {
      const sw = kf(τ, [[0, 0], [300, 0, "lin"], [950, 1, "inout"], [1350, 1, "lin"], [1470, .8, "out"], [1620, 1, "back"], [2200, 0, "inout"]]);
      return { itemA: 1, itemB: sw, headTilt: sw * 5,
        brow: kf(τ, [[1350, 0], [1470, .9, "back"], [1650, .9], [1850, 0, "inout"]]) };
    } },
};
/* the spectacles read: the eye and head sweep along lines, the glasses slide
   down the beak and get pushed back up with a nod */
const EYEWEAR_ACT: Partial<Record<FaceEyewear, { working?: Act }>> = {
  spectacles: { working: (τ) => ({
    headDX: kf(τ, [[80, -.6], [900, 1, "inout"], [1000, -.6, "out"], [1800, 1, "inout"], [1900, -.6, "out"]]),
    gaze: kf(τ, [[80, -.4], [900, .9, "inout"], [1000, -.4, "out"], [1800, .9, "inout"], [1900, -.4, "out"]]) * .4,
    slip: kf(τ, [[0, 0], [1900, .55, "lin"], [2000, .55], [2140, -.12, "back"], [2350, 0, "inout"]]),
    headTilt: kf(τ, [[2000, 0], [2110, -7, "back"], [2350, 0, "inout"]]),
  }) },
};

/* ================================================================
   pose(t): the state's loop, the temperament, the items — then the
   follow-through. The hat is a mass on the head: its displacement is the
   head's velocity convolved with a damped cosine (a spring's step
   response — lag, overshoot, settle). Sampling the pure state function at
   a few past instants gives that convolution with no hidden state, so the
   whole frame stays a pure function of t, and since every loop is
   seamless the sampling wraps the loop edge cleanly.
   ================================================================ */
function statePose(look: BotLook, state: FaceState, i: number, ph: number): Pose {
  const tp = TEMPERS[look.temper];
  const xd = EXTRA_ACT[look.extra];
  let p: Pose;
  switch (state) {
    case "idle": p = tp.idle(i, tp, ph); p.glint = (i - tp.glintAt) / 320; break;
    case "working": p = working(i, tp, ph, xd?.pad ?? true, xd?.peck ?? true); break;
    case "waiting": p = waiting(i, tp, ph, look.eyewear); break;
    default: p = asleep(i, tp, ph);
  }
  const τ = state === "working" ? i * TW : i;
  const xa = state === "asleep" ? undefined : xd?.[state];   // asleep, every item just droops
  if (xa) merge(p, xa(τ, i));
  const ea = state === "working" ? EYEWEAR_ACT[look.eyewear]?.working : undefined;
  if (ea) merge(p, ea(τ, i));
  p.glint = unit(p.glint);
  p.z1 = unit(p.z1); p.z2 = unit(p.z2); p.z3 = unit(p.z3); p.z4 = unit(p.z4);
  return p;
}

const DT = 40, TAPS = 12;
const gStiff = (t: number) => Math.exp(-t / 110) * Math.cos(2 * PI * t / 240);   // the hat body
const gSoft = (t: number) => Math.exp(-t / 190) * Math.cos(2 * PI * t / 430);    // bobble / strap / flop
const gMid = (t: number) => Math.exp(-t / 150) * Math.cos(2 * PI * t / 300);     // tools, neckwear, chain
const headCh = (p: Pose) => ({
  a: p.bodyRot + p.headTilt * 0.55 - p.tailAng * 0.12,          // trunk angle (deg)
  y: (p.bob + p.crouch * 2.2) / K + p.headDY * 0.35,            // head y (mascot units)
  x: p.headDX * 0.35,
});

function fullPose(look: BotLook, state: FaceState, t: number, ph: number): Pose {
  const L = FACE_LOOP_MS[state];
  const w = (u: number) => ((u % L) + L) % L;
  const p = statePose(look, state, w(t), ph);
  let rA = 0, rY = 0, rX = 0, sA = 0, sY = 0, sX = 0, mA = 0, mY = 0;
  let prev = headCh(p);
  for (let k = 1; k <= TAPS; k++) {
    const q = headCh(statePose(look, state, w(t - k * DT), ph));
    const gs = gStiff(k * DT), gf = gSoft(k * DT), gm = gMid(k * DT);
    const da = prev.a - q.a, dy = prev.y - q.y, dx = prev.x - q.x;
    rA -= gs * da; rY -= gs * dy; rX -= gs * dx;
    sA -= gf * da; sY -= gf * dy; sX -= gf * dx;
    mA -= gm * da; mY -= gm * dy;
    prev = q;
  }
  p.rA = rA; p.rY = rY; p.rX = rX; p.sA = sA; p.sY = sY; p.sX = sX; p.mA = mA; p.mY = mY;
  return p;
}

/** The seed picks a blink phase (so a roster never blinks in unison) and,
 *  for the two ambient loops, where in the loop this bot starts. */
const blinkPhase = (seed: number) => ((Math.floor(Math.abs(seed)) % 5) * 60);
const loopOffset = (seed: number, state: FaceState) =>
  state === "idle" || state === "asleep" ? (Math.floor(Math.abs(seed)) * 7919) % FACE_LOOP_MS[state] : 0;

/** The pure pose at loop time `ms` for a look in a state — every channel the
 *  rig reads. Exported for the tests and the harness; the rig calls it too. */
export function poseAt(look: BotLook, state: FaceState, ms: number, seed = 0): Readonly<Record<string, number>> {
  return fullPose(look, state, ms, blinkPhase(seed));
}

function lerpPose(a: Pose, b: Pose, u: number): Pose {
  const o = { ...b };
  for (const k in b) { const key = k as PoseKey; o[key] = a[key] + (b[key] - a[key]) * u; }
  return o;
}

/* ================================================================
   Rig. The bust is a square viewBox in mascot units around the head
   (the hats mockup's crop: headroom for a top hat, the collar in), and
   the face's own circle clips the rest. Layers, back to front: behind
   (a tucked pencil), body, crest, beak, hat, face (eye, eyewear, glint,
   brow — brow on top so a thick rim never buries the attitude), front
   (tools, neckwear). The pad and the z's sit outside the trunk.
   ================================================================ */
const VIEWBOX = "10 1.65 13.4 13.4";        // centre (16.7, 8.35), r 6.7
const CROWN = { x: 15.6, y: 6.6 };          // where a hat sits; hats pivot here
const NS = "http://www.w3.org/2000/svg";
type Attrs = Record<string, string | number>;
function el<T extends SVGElement>(n: string, at: Attrs = {}): T {
  const e = document.createElementNS(NS, n) as T;
  for (const k in at) e.setAttribute(k, String(at[k]));
  return e;
}
const INK = "var(--bf-ink)", RING = "var(--bf-ring)", ITEM = "var(--bf-item)", GLINT = "var(--color-text-primary)";
const FILL: Attrs = { fill: "currentColor" };
const ink = (w: number, o?: number): Attrs => ({ fill: "none", stroke: INK, "stroke-width": w,
  "stroke-linecap": "round", "stroke-linejoin": "round", ...(o !== undefined ? { opacity: o } : {}) });
const stroke = (d: string, c: string, w: number, extra: Attrs = {}) =>
  el<SVGPathElement>("path", { d, fill: "none", stroke: c, "stroke-width": w, "stroke-linecap": "round", "stroke-linejoin": "round", ...extra });
const rot = (a: number, x: number, y: number) => { const r = a * PI / 180, c = Math.cos(r), s = sin(r); return [x * c - y * s, x * s + y * c]; };
const f2 = (v: number) => v.toFixed(2);
const f3 = (v: number) => v.toFixed(3);

/* ---------- the hats: flat silhouettes in the bird's own fill, ink details,
   authored in mascot units on the crown. Each gives the lag gains for its
   mass and, optionally, a secondary-motion hook for what hangs off it. ---------- */
type Secondary = ((p: Pose) => void) | null;
interface HatDef { gains: { rot: number; dy: number }; build(g: SVGGElement): Secondary }
const HATS: Record<FaceHat, HatDef> = {
  captain: { gains: { rot: .45, dy: .6 }, build(g) {
    g.append(
      /* tall crown, higher at the front; long visor with an ink top edge */
      el("path", { ...FILL, d: "M12.6 7.2 C 12.7 5.7, 13.7 4.9, 15.2 4.65 C 16.7 4.4, 17.9 4.3, 18.5 4.75 C 18.75 5.0, 18.3 5.9, 18.05 6.65 C 16.4 7.0, 14.4 7.2, 12.6 7.2 Z" }),
      el("path", { ...FILL, d: "M16.2 6.85 C 17.7 6.4, 19.3 6.35, 20.6 6.7 C 19.6 7.2, 18.0 7.3, 16.3 7.15 Z" }),
      el("path", { ...ink(.4), d: "M12.8 7.1 C 14.5 7.2, 16.3 7.0, 18.05 6.7" }),
      el("path", { ...ink(.4), d: "M16.4 6.82 C 17.8 6.42, 19.3 6.38, 20.55 6.7" }),
    );
    return null;
  } },
  beanie: { gains: { rot: .5, dy: .7 }, build(g) {
    g.append(
      el("path", { ...FILL, d: "M12.7 7.3 C 12.55 5.6, 13.9 4.15, 15.8 4.1 C 17.65 4.05, 18.75 5.4, 18.6 7.2 C 16.7 7.0, 14.6 7.1, 12.7 7.3 Z" }),
      el("path", { ...ink(.42), d: "M12.8 7.1 C 14.6 6.9, 16.7 6.85, 18.55 7.0" }),
      el("path", { ...ink(.3, .7), d: "M12.85 6.45 C 14.7 6.25, 16.6 6.2, 18.5 6.4" }),
    );
    const bobble = el<SVGCircleElement>("circle", { cx: 15.8, cy: 3.75, r: .72, ...FILL, stroke: INK, "stroke-width": .3 });
    g.append(bobble);
    return (p) => bobble.setAttribute("transform",
      `translate(${f3(clamp(p.sX * .9 + p.sA * .035, -.6, .6))} ${f3(clamp(p.sY * .9, -.42, .35))})`);   // floats, never detaches
  } },
  hardhat: { gains: { rot: .4, dy: .65 }, build(g) {
    g.append(
      el("path", { ...FILL, d: "M12.5 7.15 C 12.55 5.5, 13.9 4.45, 15.7 4.4 C 17.5 4.35, 18.9 5.45, 19.0 7.05 Z" }),
      el("path", { ...FILL, d: "M11.8 7.35 C 13.5 6.95, 17.5 6.85, 19.9 7.1 C 19.65 7.6, 17.6 7.8, 15.6 7.75 C 13.8 7.72, 12.4 7.72, 11.8 7.35 Z" }),
      el("path", { ...ink(.4), d: "M12.0 7.3 C 13.6 6.98, 17.4 6.9, 19.8 7.12" }),
    );
    /* chin strap hangs from the back of the brim, short and quiet */
    const strap = el<SVGPathElement>("path", { ...ink(.34, .6), d: "M0 0 C -0.15 0.7, -0.3 1.4, -0.1 2.0" });
    const sg = el<SVGGElement>("g", { transform: "translate(12.9 7.7)" });
    sg.append(strap); g.append(sg);
    return (p) => strap.setAttribute("transform", `rotate(${f2(clamp(-p.sX * 35 + p.sA * 1.0, -28, 28))})`);
  } },
  beret: { gains: { rot: .5, dy: .7 }, build(g) {
    const disc = el<SVGGElement>("g");
    disc.append(
      /* wide disc, slumped well past the nape */
      el("path", { ...FILL, d: "M10.5 7.0 C 11.0 5.45, 14.0 4.8, 16.7 5.2 C 18.0 5.4, 18.7 6.1, 18.4 6.68 C 16.6 6.88, 14.0 7.15, 11.8 7.4 C 11.0 7.45, 10.4 7.3, 10.5 7.0 Z" }),
      el("path", { ...ink(.36), d: "M11.6 7.28 C 14.0 7.08, 16.6 6.85, 18.3 6.62" }),
      el("circle", { cx: 14.3, cy: 4.95, r: .34, ...FILL, stroke: INK, "stroke-width": .28 }),
    );
    g.append(disc);
    /* the flop: the disc pivots at the front of the crown so the overhang swings */
    return (p) => disc.setAttribute("transform", `rotate(${f2(clamp(p.sA * .9, -9, 9))} 17.3 6.6)`);
  } },
  deerstalker: { gains: { rot: .45, dy: .65 }, build(g) {
    g.append(
      /* two brims that actually stick out, a sparse tweed, a bow of tied flaps */
      el("path", { ...FILL, d: "M12.5 7.2 C 12.65 5.45, 13.95 4.7, 15.7 4.7 C 17.45 4.7, 18.6 5.45, 18.7 7.1 Z" }),
      el("path", { ...FILL, d: "M17.2 6.95 C 18.4 6.5, 19.9 6.45, 20.9 6.9 C 20.0 7.35, 18.4 7.38, 17.2 7.25 Z" }),
      el("path", { ...FILL, d: "M13.4 7.3 C 12.2 7.0, 10.9 7.05, 10.1 7.5 C 10.9 7.9, 12.4 7.85, 13.4 7.6 Z" }),
      el("path", { ...ink(.36), d: "M12.6 7.05 C 14.6 6.9, 16.7 6.85, 18.6 6.95" }),
      el("path", { ...ink(.22, .5), d: "M13.6 6.75 L 15.3 4.95 M15.4 6.75 L 17.1 5.0 M14.4 5.15 L 16.2 6.75" }),
    );
    const bow = el<SVGPathElement>("path", { ...FILL, stroke: INK, "stroke-width": .24, "stroke-linejoin": "round",
      d: "M-0.85 0.1 C -1.3 -0.6, -0.4 -0.95, 0 -0.2 C 0.4 -0.95, 1.3 -0.6, 0.85 0.1 Z" });
    const bg = el<SVGGElement>("g", { transform: "translate(15.7 4.8) scale(1.3)" });
    bg.append(bow); g.append(bg);
    return (p) => bow.setAttribute("transform",
      `translate(0 ${f3(clamp(p.sY * .5, -.5, .3))}) rotate(${f2(clamp(p.sA * .7, -10, 10))})`);
  } },
  tophat: { gains: { rot: .9, dy: .6 }, build(g) {
    g.append(
      el("path", { ...FILL, d: "M13.3 6.95 L 13.45 3.7 C 14.5 3.3, 17.1 3.32, 18.15 3.75 L 18.2 6.55 C 16.5 6.9, 15.0 7.05, 13.3 6.95 Z" }),
      el("path", { ...FILL, d: "M12.2 7.2 C 14.0 6.72, 17.5 6.55, 19.55 6.8 C 19.35 7.3, 17.4 7.48, 15.6 7.48 C 14.0 7.48, 12.6 7.5, 12.2 7.2 Z" }),
      el("path", { ...ink(.4), d: "M12.4 7.15 C 14.1 6.72, 17.4 6.6, 19.45 6.8" }),
      el("path", { ...ink(.46), d: "M13.4 6.35 C 15.0 6.08, 16.8 5.98, 18.15 6.12" }),
    );
    return null;
  } },
};

/* ---------- eyewear: rides the face group (the head's peek and the slip);
   `lens` is the part that pops / flips, `glintR` the ring the light sweeps ---------- */
const CHAIN_D = "M17.6 10.8 C 17.8 11.7, 17.5 12.5, 16.9 13.1";
const CHAIN_TUCK_D = "M17.6 10.8 C 17.8 11.3, 17.7 11.8, 17.35 12.15";   // into the bow tie's knot
interface Eyewear { glintR: number | null; apply(p: Pose, lagA: number): string }   // returns the lens transform (for the glint)
function buildEyewear(kind: FaceEyewear, extra: FaceExtra, face: SVGGElement): Eyewear {
  const st: Attrs = { fill: "none", stroke: RING, "stroke-linecap": "round", "stroke-linejoin": "round" };
  const g = el<SVGGElement>("g");
  face.append(g);
  const slipT = (p: Pose, kx: number, ky: number) => `translate(${f2(p.slip * kx + p.pop * .15)} ${f2(p.slip * ky - p.pop * .7 + p.lensDY)})`;
  switch (kind) {
    case "monocle": {
      const chain = el<SVGPathElement>("path", { d: extra === "bowtie" ? CHAIN_TUCK_D : CHAIN_D, ...st, "stroke-width": .5 });
      g.append(el("circle", { cx: 17, cy: 9, r: 1.95, ...st, "stroke-width": .6 }), chain);
      return { glintR: 1.95, apply(p, lagA) {
        const t = slipT(p, .25, .55);
        g.setAttribute("transform", t);
        chain.setAttribute("transform", `rotate(${f2(-14 * p.pop + p.lensRot * .3 + lagA * 2.5)} 17.6 10.8)`);
        return t;
      } };
    }
    case "pincenez": {
      /* small lens pinched on the beak: a bridge spring over the beak's base, a ribbon to the collar */
      const ribbon = el<SVGPathElement>("path", { d: "M16.35 10.55 C 16.05 11.4, 15.6 12.1, 15.0 12.7", ...st, "stroke-width": .42 });
      g.append(el("circle", { cx: 17, cy: 9, r: 1.75, ...st, "stroke-width": .55 }),
        el("path", { d: "M18.7 8.55 C 19.1 7.95, 19.75 7.95, 20.15 8.6", ...st, "stroke-width": .5 }), ribbon);
      return { glintR: 1.75, apply(p, lagA) {
        const t = slipT(p, .25, .55);
        g.setAttribute("transform", t);
        ribbon.setAttribute("transform", `rotate(${f2(14 * p.pop - p.lensRot * .3 - lagA * 2.5)} 16.35 10.55)`);
        return t;
      } };
    }
    case "round":
      g.append(el("circle", { cx: 17, cy: 9, r: 2.25, ...st, "stroke-width": .8 }),
        el("path", { d: "M14.75 8.75 L13.1 8.15", ...st, "stroke-width": .55 }));
      return { glintR: 2.25, apply(p) {
        const t = `${slipT(p, .15, .4)} rotate(${f2(p.slip * 12)} 17 9)`;
        g.setAttribute("transform", t); return t;
      } };
    case "rect":
      g.append(el("rect", { x: 15.25, y: 7.55, width: 3.6, height: 2.9, rx: .55, ...st, "stroke-width": .55 }),
        el("path", { d: "M15.25 8.2 L13.4 7.7", ...st, "stroke-width": .5 }));
      return { glintR: null, apply(p) {
        const t = `${slipT(p, .15, .4)} rotate(${f2(p.slip * 12)} 17 9)`;
        g.setAttribute("transform", t); return t;
      } };
    case "loupe": {
      /* the band rides the head; the lens + barrel hinge at the band's front */
      const lens = el<SVGGElement>("g");
      lens.append(el("circle", { cx: 17, cy: 9, r: 1.8, ...st, "stroke-width": .6 }),
        /* the barrel: one solid piece so it still reads as a lump at 28 px */
        el("path", { d: "M18.4 7.7 L 19.9 8.15 L 19.9 9.85 L 18.4 10.3 Z", fill: RING, "stroke-linejoin": "round" }));
      g.append(el("path", { d: "M16.2 7.0 C 15.1 6.2, 13.8 6.25, 12.8 6.95", ...st, "stroke-width": .55 }), lens);
      return { glintR: 1.8, apply(p) {
        g.setAttribute("transform", `translate(0 ${f2(p.lensDY)})`);
        const t = `translate(0 ${f2(p.lensDY - p.pop * .4)}) rotate(${f2(p.slip * 22 + p.lensRot)} 16.5 7.3)`;
        lens.setAttribute("transform", t); return t;
      } };
    }
    case "goggles":
      g.append(el("path", { d: "M14.9 8.4 C 13.7 7.85, 12.5 7.85, 11.4 8.45", ...st, "stroke-width": .75 }),
        el("rect", { x: 14.9, y: 7.3, width: 4.4, height: 3.4, rx: 1.4, fill: RING, "fill-opacity": .18, stroke: RING, "stroke-width": .6 }));
      return { glintR: null, apply(p) {
        const t = `translate(${f2(p.slip * -.1)} ${f2(p.slip * .45 + p.lensDY - p.pop * .5)}) rotate(${f2(p.slip * 5)} 17 9)`;
        g.setAttribute("transform", t); return t;
      } };
    default: {   // spectacles: the monocle's twin — thick rim, temple arm with an ear hook, bridge hump over the beak, no chain
      g.append(el("circle", { cx: 17, cy: 9, r: 1.95, ...st, "stroke-width": .9 }),
        el("path", { d: "M15.1 8.7 C 14.3 8.45, 13.3 8.3, 12.4 8.45 C 12.05 8.5, 11.9 8.85, 12.1 9.3", ...st, "stroke-width": .7 }),
        el("path", { d: "M18.9 8.7 C 19.3 8.1, 19.95 8.15, 20.4 8.75", ...st, "stroke-width": .7 }));
      return { glintR: 1.95, apply(p) {
        const t = `${slipT(p, .25, .55)} rotate(${f2(p.slip * 9)} 17 9)`;
        g.setAttribute("transform", t); return t;
      } };
    }
  }
}

/* ---------- the extras: neckwear and tools, from the tools + character
   mockups. `apply` may return an eye scale (the magnifier). ---------- */
interface Layers { behind: SVGGElement; front: SVGGElement }
interface Extra { apply(p: Pose, lagA: number, lagY: number): number | void }
const BOW_L = "M17.3 12.4 L15.7 11.45 L15.7 13.35 Z", BOW_R = "M17.3 12.4 L18.9 11.55 L18.9 13.25 Z";
/* the crest: with a hat on every head it moved from the crown to the nape —
   three spikes fanning back from under the hat's back edge */
const CREST_D = "M13.7 6.75 L12.75 5.1 L12.55 6.9 L11.55 5.45 L11.95 7.05 L10.7 6.1 L11.4 7.4 Z";
function buildExtra(kind: FaceExtra, L: Layers, crestSlot: SVGGElement): Extra | null {
  switch (kind) {
    case "bowtie": {
      const bow = el<SVGGElement>("g");
      bow.append(el("path", { d: BOW_L, fill: ITEM }), el("path", { d: BOW_R, fill: ITEM }),
        el("circle", { cx: 17.3, cy: 12.4, r: .38, fill: INK }));
      L.front.append(bow);
      return { apply: (_p, lagA) => bow.setAttribute("transform", `rotate(${f2(clamp(-lagA * .9, -8, 8))} 17.3 12.4)`) };
    }
    case "tie": {
      /* a knot under the chin and a blade down the chest; swings a little with the head */
      const tie = el<SVGGElement>("g");
      tie.append(
        el("path", { d: "M17.15 12.05 L17.95 12.35 L17.25 14.75 L16.35 14.25 Z", fill: ITEM, stroke: INK, "stroke-width": .18, "stroke-linejoin": "round" }),
        el("path", { d: "M17.05 11.65 L17.9 11.75 L17.95 12.4 L17.1 12.25 Z", fill: ITEM, stroke: INK, "stroke-width": .18, "stroke-linejoin": "round" }));
      L.front.append(tie);
      return { apply: (p, lagA) => tie.setAttribute("transform", `rotate(${f2(clamp(-lagA * 1.2, -10, 10) - p.droop * 4)} 17.5 12.0)`) };
    }
    case "crest": {
      const crest = el<SVGPathElement>("path", { d: CREST_D, fill: "currentColor", stroke: "currentColor", "stroke-width": .3, "stroke-linejoin": "round" });
      crestSlot.append(crest);
      return { apply: (p, lagA) => crest.setAttribute("transform", `rotate(${f2(clamp(-lagA * 2, -14, 14) - p.droop * 6)} 12.9 7.2)`) };
    }
    case "scarf": {
      /* band across the neck, knot at the back, two tails that flutter and lag */
      const t1 = el<SVGPathElement>("path", { d: "M11.9 10.6 L9.9 9.5 L10.35 10.8 Z", fill: ITEM });
      const t2 = el<SVGPathElement>("path", { d: "M11.9 11.0 L9.8 11.8 L10.65 12.2 Z", fill: ITEM });
      L.front.append(t1, t2,
        el("path", { d: "M11.8 10.3 C 13.3 11.6, 15.3 12.4, 17.7 12.3 C 17.3 12.9, 16.7 13.35, 16.0 13.55 C 13.9 13.25, 12.4 12.35, 11.3 11.3 Z", fill: ITEM, stroke: INK, "stroke-width": .16 }),
        el("circle", { cx: 11.9, cy: 10.8, r: .62, fill: ITEM, stroke: INK, "stroke-width": .16 }));
      return { apply(p, lagA, lagY) {
        const y = f2(lagY * .35);
        t1.setAttribute("transform", `translate(0 ${y}) rotate(${f2(p.itemA + lagA * 2.2 + p.droop * 26)} 11.9 10.65)`);
        t2.setAttribute("transform", `translate(0 ${y}) rotate(${f2(-p.itemA * .7 + lagA * 1.8 + p.droop * 18)} 11.9 10.95)`);
      } };
    }
    case "headset": {
      /* ink cup on the cheek, ink band up to the crown, boom (light underlay + ink core so it reads over the head AND the ground) */
      const BOOM = "M14.6 10.3 C 15.3 11.9, 17.3 12.65, 19.2 12.35";
      const boom = el<SVGGElement>("g");
      boom.append(stroke(BOOM, "currentColor", .9), stroke(BOOM, INK, .34),
        el("circle", { cx: 19.75, cy: 12.3, r: .66, fill: "currentColor" }), el("circle", { cx: 19.75, cy: 12.3, r: .32, fill: INK }));
      L.front.append(stroke("M14.55 8.75 C 14.35 7.75, 14.75 6.85, 15.6 6.45 C 15.9 6.32, 16.2 6.32, 16.5 6.4", INK, .5),
        el("rect", { x: 13.6, y: 8.35, width: 1.5, height: 2.1, rx: .6, fill: INK }), boom);
      return { apply: (p, lagA, lagY) => boom.setAttribute("transform",
        `translate(0 ${f2(lagY * .3)}) rotate(${f2(lagA * 1.6 + p.droop * 30)} 14.5 10.2)`) };
    }
    case "spanner": {
      /* open jaw up, ring end down, gripped mid-handle by the beak; one fill with an ink keyline */
      const D = "M-.33 -2.9 L-.33 -1.9 L.33 -1.9 L.33 -2.9 A.95 .95 0 1 1 -.33 -2.9 Z M-.36 -1.1 H.36 V2.1 H-.36 Z" +
        " M-.68 2.75 a.68 .68 0 1 0 1.36 0 a.68 .68 0 1 0 -1.36 0 Z M-.27 2.75 a.27 .27 0 1 1 .54 0 a.27 .27 0 1 1 -.54 0 Z";
      const g = el<SVGGElement>("g");
      g.append(el("path", { d: D, fill: "currentColor", stroke: INK, "stroke-width": .2, "stroke-linejoin": "round" }));
      L.front.append(g);
      return { apply: (p, lagA, lagY) => g.setAttribute("transform",
        `translate(20.3 ${f2(10.6 + p.droop * .6 + lagY * .3)}) rotate(${f2(-28 + p.itemA + lagA * 1.2 + p.droop * 88)}) scale(1.1)`) };
    }
    case "pencil": {
      /* tucked behind the head (tip up past the nape, under the hat's back edge);
         pulled out to the beak when working and used on the pad at the chest */
      const mk = () => {
        const g = el<SVGGElement>("g");
        g.append(
          el("rect", { x: -2.7, y: -.47, width: 4.6, height: .94, rx: .18, fill: "currentColor", stroke: INK, "stroke-width": .2 }),
          el("path", { d: "M1.9 -.47 L2.95 0 L1.9 .47 Z", fill: "currentColor", stroke: INK, "stroke-width": .2, "stroke-linejoin": "round" }),
          el("path", { d: "M2.55 -.17 L2.95 0 L2.55 .17 Z", fill: INK }),
          stroke("M-2.15 -.47 V.47 M-1.85 -.47 V.47", INK, .16));
        return g;
      };
      const tucked = mk(), held = mk();
      L.behind.append(tucked); L.front.append(held);
      const T = { x: 12.9, y: 7.9, a: -118 }, H = { x: 21.6, y: 9.7, a: 125 };
      return { apply(p, lagA) {
        const u = p.itemA;
        const x = T.x + (H.x - T.x) * u + p.itemB * u, y = T.y + p.droop * .5 + (H.y + p.itemC * .9 - T.y) * u;
        const a = T.a + p.droop * 10 + (H.a - T.a) * u + lagA * .35 * (1 - u);
        const out = u > .35;
        tucked.style.visibility = out ? "hidden" : "visible";
        held.style.visibility = out ? "visible" : "hidden";
        (out ? held : tucked).setAttribute("transform", `translate(${f2(x)} ${f2(y)}) rotate(${f2(a)}) scale(1.2)`);
      } };
    }
    case "magnifier": {
      /* lens + handle, handle end in the beak. Hangs at rest; comes up over the eye when working (the eye magnifies under it) */
      const g = el<SVGGElement>("g");
      g.append(el("circle", { cx: 0, cy: 0, r: 2.1, fill: "currentColor", "fill-opacity": .12, stroke: "currentColor", "stroke-width": .6 }),
        el("circle", { cx: 0, cy: 0, r: 2.1, fill: "none", stroke: INK, "stroke-width": .2 }),
        stroke("M1.55 1.55 L2.6 2.6", "currentColor", .9), stroke("M1.55 1.55 L2.6 2.6", INK, .3));
      L.front.append(g);
      const HANG = [17.5, 12.7, -80], UP = [17.1, 9.05, -20], CHEST = [18.6, 12.6, -65];
      return { apply(p, lagA) {
        const q = HANG.map((v, i) => v + (UP[i] - v) * p.itemA + (CHEST[i] - UP[i]) * p.itemB);   // lens centre + angle
        const [hx, hy] = rot(q[2], 2.6, 2.6), gx = q[0] + hx, gy = q[1] + hy;                     // the grip (handle end)
        g.setAttribute("transform",
          `rotate(${f2(lagA * 1.4 + p.droop * 28)} ${f2(gx)} ${f2(gy)}) translate(${f2(q[0])} ${f2(q[1] + p.droop * .4)}) rotate(${f2(q[2])})`);
        const d = Math.hypot(q[0] - (17 + p.gaze + p.headDX * .35), q[1] - (9 + p.headDY * .35));
        return 1 + .5 * clamp01(1.3 - d / 1.3);                                                     // eye scale under the lens
      } };
    }
    default: return null;
  }
}

interface Scene { root: SVGGElement; apply(p: Pose): void }
function buildScene(look: BotLook): Scene {
  const root = el<SVGGElement>("g");                    // mascot coordinates
  const trunk = el<SVGGElement>("g"), breatheG = el<SVGGElement>("g");
  const behind = el<SVGGElement>("g"), crestSlot = el<SVGGElement>("g"), front = el<SVGGElement>("g");
  const body = el<SVGPathElement>("path", { d: BODY_D, ...FILL });
  const beak = el<SVGPathElement>("path", { d: BEAK_D, ...FILL });

  const hatDef = HATS[look.hat];
  const hat = el<SVGGElement>("g");
  const hatSecondary = hatDef.build(hat);

  const face = el<SVGGElement>("g");
  const eye = el<SVGEllipseElement>("ellipse", { cx: 17, cy: 9, rx: .95, ry: .95, fill: INK });
  face.append(eye);
  const eyewear = buildEyewear(look.eyewear, look.extra, face);
  /* the glint: a short bright arc that sweeps once around the ring */
  const glint = eyewear.glintR === null ? null : el<SVGCircleElement>("circle", { cx: 17, cy: 9, r: eyewear.glintR, fill: "none",
    stroke: GLINT, "stroke-width": .7, pathLength: 1, "stroke-dasharray": ".13 .87", "stroke-linecap": "round" });
  if (glint) { glint.style.visibility = "hidden"; face.append(glint); }
  const brow = el<SVGPathElement>("path", { d: browD(0), ...ink(.75) });
  face.append(brow);                                   // brow on top: thick rims must not bury the attitude

  breatheG.append(behind, body, crestSlot, beak, hat, face, front);
  trunk.append(breatheG);
  root.append(trunk);
  const extra = buildExtra(look.extra, { behind, front }, crestSlot);

  /* note pad: sits in front of him, low right, under where the beak lands */
  const pad = el<SVGGElement>("g");
  const padFlipG = el<SVGGElement>("g");
  padFlipG.append(el("rect", { x: 0, y: 0, width: 4.8, height: 3.4, rx: .35, ...FILL, stroke: INK, "stroke-width": .3 }));
  const mkLine = (d: string) => el<SVGPathElement>("path", { ...ink(.36), pathLength: 1, "stroke-dasharray": 1, "stroke-dashoffset": 1, d });
  const line1 = mkLine("M0.7 1.05 C 1.2 0.75, 1.7 1.25, 2.3 0.95 C 2.8 0.7, 3.3 1.15, 3.9 0.95");
  const line2 = mkLine("M0.7 2.2 C 1.2 1.9, 1.7 2.4, 2.2 2.15 C 2.6 1.95, 2.9 2.2, 3.1 2.1");
  padFlipG.append(line1, line2);
  pad.append(padFlipG);
  pad.style.visibility = "hidden";
  root.append(pad);

  /* sleepy z's: a stroked zigzag (no font dependency), drift up-right off the crown */
  const Z_SCALES = [1.5, 1.1, 1.5, 1.1];
  const zs = Z_SCALES.map((sc) => {
    const z = el<SVGPathElement>("path", { d: "M0 0 H1.3 L0 1.3 H1.3", fill: "none", stroke: "currentColor",
      "stroke-width": (.42 / sc).toFixed(3), "stroke-linecap": "round", "stroke-linejoin": "round" });
    z.style.visibility = "hidden"; root.append(z); return z;
  });

  function apply(p: Pose): void {
    const dy = (p.bob + p.crouch * 2.2) / K;
    const tilt = p.bodyRot + p.headTilt * 0.55 - p.tailAng * 0.12;
    trunk.setAttribute("transform", `translate(0 ${f3(dy)}) rotate(${f2(tilt)} 13 11.5)`);
    breatheG.setAttribute("transform",
      `translate(${FEET.x} ${FEET.y}) scale(1 ${(1 + p.breathe).toFixed(4)}) translate(${-FEET.x} ${-FEET.y})`);
    const fx = p.headDX * 0.35, fy = p.headDY * 0.35;
    face.setAttribute("transform", `translate(${f2(fx)} ${f2(fy)})`);
    brow.setAttribute("d", browD(p.brow));
    beak.setAttribute("transform", `rotate(${f2(p.beak)} 17.5 10.4)`);

    const lagA = clamp(p.mA, -16, 16), lagY = clamp(p.mY * .625, -1.2, 1.2);
    const es = (extra?.apply(p, lagA, lagY) as number | undefined) || 1;   // an item may magnify the eye
    eye.setAttribute("cx", f2(17 + p.gaze));
    eye.setAttribute("rx", f2(.95 * p.eyeScale * es));
    eye.setAttribute("ry", f2(.95 * p.eyeScale * es * p.blink));
    const lensT = eyewear.apply(p, lagA);
    if (glint) {
      if (p.glint > 0) {
        glint.style.visibility = "visible";
        glint.setAttribute("stroke-dashoffset", f3(0.62 - p.glint * 0.55));
        glint.style.opacity = f3(sin(PI * p.glint) * 0.9);
        glint.setAttribute("transform", lensT);
      } else glint.style.visibility = "hidden";
    }

    /* the hat rides the face translation, lags the head through its spring */
    const gn = hatDef.gains;
    const hRot = clamp(p.rA * gn.rot, -9, 9);
    const hDY = clamp(p.rY * gn.dy, -1.2, 0.3);   // may float up a lot, sink in only a little
    const hDX = clamp(p.rX * 0.4, -.5, .5);
    hat.setAttribute("transform", `translate(${f3(fx + hDX)} ${f3(fy + hDY)}) rotate(${f2(hRot)} ${CROWN.x} ${CROWN.y})`);
    if (hatSecondary) hatSecondary(p);

    if (p.noteVis > 0.01) {
      pad.style.visibility = "visible";
      pad.style.opacity = f3(p.noteVis);
      pad.setAttribute("transform", `translate(18.6 ${f2(11.5 + (1 - p.noteVis) * 1.5)}) rotate(${f2(p.jitter * 2.5)})`);
      padFlipG.setAttribute("transform", `scale(1 ${f3(p.padFlip)})`);
      const la = f3(p.lineA);
      line1.setAttribute("stroke-dashoffset", f3(1 - clamp01(p.scribble * 2)));
      line2.setAttribute("stroke-dashoffset", f3(1 - clamp01(p.scribble * 2 - 1)));
      line1.style.opacity = la; line2.style.opacity = la;
    } else pad.style.visibility = "hidden";

    const zph = [p.z1, p.z2, p.z3, p.z4];
    for (let i = 0; i < 4; i++) {
      const z = zs[i], ph = zph[i];
      if (ph > 0) {
        z.style.visibility = "visible";
        z.setAttribute("transform", `translate(${f2(18.3 + ph * 1.7)} ${f2(6.3 - ph * 3.1)}) scale(${Z_SCALES[i]})`);
        z.style.opacity = f3(sin(PI * ph) * 0.75);
      } else z.style.visibility = "hidden";
    }
  }
  return { root, apply };
}

/* ================================================================
   Faces: one shared rAF loop for all of them, started by the first
   createBotFace and stopped when none remain. Each face keeps its own
   state and the time it entered it, so a switch restarts that state's
   loop at 0 while a 300 ms blend from the previous state's last pose
   covers whatever gesture it was mid-way through.
   ================================================================ */
export interface BotFace {
  readonly el: SVGSVGElement;
  setState(s: FaceState): void;
  setLook(l: BotLook): void;
  setColorIndex(i: number): void;
  dispose(): void;
}

const FADE_MS = 300;                                   // state cross-fade
/** the frame each state holds when motion is reduced */
const REST_T: Readonly<Record<FaceState, number>> = { idle: 0, working: 700, waiting: 1000, asleep: 3000 };

interface FaceImpl {
  el: SVGSVGElement; look: BotLook; state: FaceState; seed: number;
  scene: Scene; t0: number; prev: Pose | null; switchAt: number; last: Pose | null;
}
const faces = new Set<FaceImpl>();
let raf = 0;
let frozenAt: number | null = null;
let colorMode = false;

/* the media query is resolved once and then read (it stays live) — this runs
   per face per frame, and a fresh matchMedia() each time is needless work */
let rmq: MediaQueryList | null | undefined;
const reducedMotion = (): boolean => {
  if (rmq === undefined) rmq = typeof window !== "undefined" ? window.matchMedia?.("(prefers-reduced-motion: reduce)") ?? null : null;
  return !!rmq?.matches;
};

function renderFace(f: FaceImpl, now: number): void {
  const ph = blinkPhase(f.seed);
  if (frozenAt !== null) { f.scene.apply(fullPose(f.look, f.state, frozenAt, ph)); return; }
  if (reducedMotion()) { f.scene.apply(fullPose(f.look, f.state, REST_T[f.state], ph)); return; }
  let p = fullPose(f.look, f.state, now - f.t0 + loopOffset(f.seed, f.state), ph);
  if (f.prev) {
    const u = clamp01((now - f.switchAt) / FADE_MS);
    if (u < 1) p = lerpPose(f.prev, p, EASES.out(u)); else f.prev = null;
  }
  f.last = p;
  f.scene.apply(p);
}

function tick(now: number): void {
  raf = 0;
  if (!faces.size) return;
  if (typeof document === "undefined" || !document.hidden) {
    for (const f of faces) if (f.el.isConnected) renderFace(f, now);
  }
  raf = requestAnimationFrame(tick);
}
function ensureLoop(): void {
  if (raf || !faces.size || frozenAt !== null || reducedMotion()) return;
  raf = requestAnimationFrame(tick);
}
function stopLoop(): void {
  if (raf) { cancelAnimationFrame(raf); raf = 0; }
}

/** Build a face. Caller appends `el` to a sized circle (the room's 28 px
 *  avatar slot) and drives it through setState / setLook. The seed offsets
 *  the blink phase and the ambient loops so a roster never moves in unison. */
export function createBotFace(look: BotLook, colorIndex: number, state: FaceState = "idle", seed = 0): BotFace {
  const svg = el<SVGSVGElement>("svg", { viewBox: VIEWBOX, "aria-hidden": "true", focusable: "false" });
  svg.classList.add("bot-face");
  svg.style.setProperty("--bf-ink", EYE_INK);
  if (colorMode) svg.classList.add("bot-face--color");
  const f: FaceImpl = { el: svg, look: { ...look }, state, seed, scene: buildScene(look), t0: 0, prev: null, switchAt: 0, last: null };
  svg.append(f.scene.root);
  const setIndex = (i: number) => svg.setAttribute("data-color-index", String(Math.max(0, Math.floor(i)) % 6));
  setIndex(colorIndex);
  svg.setAttribute("data-state", state);

  const now = () => (typeof performance !== "undefined" ? performance.now() : Date.now());
  f.t0 = now();
  faces.add(f);
  renderFace(f, f.t0);                                 // a deterministic first frame, even before the loop's first tick
  ensureLoop();

  return {
    el: svg,
    setState(s) {
      if (s === f.state) return;
      const t = now();
      f.prev = f.last; f.switchAt = t; f.t0 = t; f.state = s;
      svg.setAttribute("data-state", s);
      if (!raf) renderFace(f, t);
      ensureLoop();
    },
    setLook(l) {
      f.look = { ...l };
      f.scene.root.remove();
      f.scene = buildScene(f.look);
      svg.append(f.scene.root);
      renderFace(f, now());
    },
    setColorIndex: setIndex,
    dispose() {
      faces.delete(f);
      svg.remove();
      if (!faces.size) stopLoop();
    },
  };
}

/** Colour mode, global: off is the ink mascot on a neutral circle; on, every
 *  face takes its tag colour. Re-tints every mounted face; new faces follow. */
export function setFaceColorMode(on: boolean): void {
  colorMode = !!on;
  for (const f of faces) f.el.classList.toggle("bot-face--color", colorMode);
}
export function faceColorMode(): boolean { return colorMode; }

/** Pin every face at loop time `ms` (null = live again). For the harness and
 *  captures: with a frozen clock a frame is reproducible by number. */
export function freezeFaces(ms: number | null): void {
  frozenAt = ms;
  if (ms !== null) {
    stopLoop();
    for (const f of faces) { f.prev = null; renderFace(f, ms); }
    return;
  }
  const t = typeof performance !== "undefined" ? performance.now() : Date.now();
  for (const f of faces) { f.prev = null; f.t0 = t; }
  ensureLoop();
}
