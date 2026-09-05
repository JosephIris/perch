// Bot faces: the team room's avatars are one rig (bot-face.ts) whose every
// frame is a pure pose(t) of (look, state). Three things are the contract:
//
//  - normalizeLook never throws and never hands the rig something it can't
//    wear — the host stores these, and a stale or hand-edited value must
//    fall back to a face, not a blank.
//  - every loop is seamless: each state starts AND ends on the neutral perch
//    for every combination of hat, eyewear, extra and temperament, so states
//    chain and the 300 ms cross-fade never has to hide a pop.
//  - the four states read as four states: their midpoints differ.
//
// Pure functions only; no DOM.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  normalizeLook, poseAt,
  FACE_HATS, FACE_EYEWEAR, FACE_EXTRAS, FACE_TEMPERS, FACE_STATES, FACE_LOOP_MS,
} from "../src/bot-face.js";
import type { BotLook, FaceState } from "../src/bot-face.js";

const DEFAULTS: BotLook = { hat: "beanie", eyewear: "monocle", extra: "none", temper: "steady" };

test("normalizeLook: garbage in, a wearable look out", () => {
  assert.deepEqual(normalizeLook(null), DEFAULTS);
  assert.deepEqual(normalizeLook(undefined), DEFAULTS);
  assert.deepEqual(normalizeLook({}), DEFAULTS);
  // unknown values fall back per field, known ones survive
  assert.deepEqual(normalizeLook({ hat: "fez", eyewear: "goggles", extra: "cape", temper: "wary" }),
    { hat: "beanie", eyewear: "goggles", extra: "none", temper: "wary" });
  // case and whitespace are forgiven; the stored value is normalised
  assert.deepEqual(normalizeLook({ hat: " TopHat ", eyewear: "PinceNez", extra: "Scarf", temper: "LEAD" }),
    { hat: "tophat", eyewear: "pincenez", extra: "scarf", temper: "lead" });
  // wrong types never throw
  for (const junk of [42, true, [], () => 0, Symbol("x"), { toString: null }] as unknown[]) {
    assert.deepEqual(normalizeLook({ hat: junk as string, eyewear: junk as string, extra: junk as string, temper: junk as string }), DEFAULTS);
  }
  assert.deepEqual(normalizeLook("beanie" as unknown as Record<string, string>), DEFAULTS);
  // every published value round-trips
  for (const hat of FACE_HATS) assert.equal(normalizeLook({ hat }).hat, hat);
  for (const eyewear of FACE_EYEWEAR) assert.equal(normalizeLook({ eyewear }).eyewear, eyewear);
  for (const extra of FACE_EXTRAS) assert.equal(normalizeLook({ extra }).extra, extra);
  for (const temper of FACE_TEMPERS) assert.equal(normalizeLook({ temper }).temper, temper);
});

/* The pad's writing is the one deliberate seam: the lines are fully drawn
   but faded out at the loop's end, undrawn and opaque at its start — both
   invisible, so the page flip reads as a fresh sheet. Everything else must
   match across the seam. */
const SEAM_EXEMPT = new Set(["scribble", "lineA"]);
const SEAM_TOL = 0.2;

test("every look loops seamlessly in every state", () => {
  let combos = 0;
  for (const hat of FACE_HATS) for (const eyewear of FACE_EYEWEAR) for (const extra of FACE_EXTRAS) for (const temper of FACE_TEMPERS) {
    const look: BotLook = { hat, eyewear, extra, temper };
    for (const state of FACE_STATES) {
      const L = FACE_LOOP_MS[state];
      const a = poseAt(look, state, 0), b = poseAt(look, state, L - 1);
      const keys = Object.keys(a);
      assert.ok(keys.length > 20, "a pose has channels");
      for (const k of keys) {
        assert.ok(Number.isFinite(a[k]), `${state} ${JSON.stringify(look)} ${k} finite at 0`);
        assert.ok(Number.isFinite(b[k]), `${state} ${JSON.stringify(look)} ${k} finite at loop end`);
        if (SEAM_EXEMPT.has(k)) continue;
        assert.ok(Math.abs(a[k] - b[k]) < SEAM_TOL,
          `${state} ${JSON.stringify(look)}: ${k} jumps at the seam (${a[k].toFixed(3)} → ${b[k].toFixed(3)})`);
      }
      // the writing's visible amount (drawn × opaque) is continuous, ~0 at both ends
      assert.ok(a.scribble * a.lineA < 0.05 && b.scribble * b.lineA < 0.05, `${state}: the pad's lines pop at the seam`);
      combos++;
    }
  }
  assert.equal(combos, FACE_HATS.length * FACE_EYEWEAR.length * FACE_EXTRAS.length * FACE_TEMPERS.length * FACE_STATES.length);
});

test("the neutral perch is the same perch for every loop", () => {
  // at t=0 every state sits on the bot's neutral perch: same head, same body
  const look = DEFAULTS;
  const ref = poseAt(look, "idle", 0);
  for (const state of FACE_STATES) {
    const p = poseAt(look, state, 0);
    for (const k of ["headTilt", "headDX", "headDY", "bodyRot", "bob", "crouch", "tailAng", "blink", "brow", "slip"]) {
      assert.ok(Math.abs(p[k] - ref[k]) < 0.05, `${state} starts off the perch on ${k} (${p[k]} vs ${ref[k]})`);
    }
  }
});

test("the four states differ at their midpoint", () => {
  const MOTION = ["headTilt", "headDX", "headDY", "bodyRot", "crouch", "blink", "brow", "eyeScale", "noteVis"];
  const dist = (a: Readonly<Record<string, number>>, b: Readonly<Record<string, number>>) =>
    MOTION.reduce((s, k) => s + Math.abs(a[k] - b[k]), 0);
  for (const temper of FACE_TEMPERS) for (const extra of FACE_EXTRAS) {
    const look: BotLook = { ...DEFAULTS, temper, extra };
    const mid = (s: FaceState) => poseAt(look, s, FACE_LOOP_MS[s] / 2);
    const m = Object.fromEntries(FACE_STATES.map((s) => [s, mid(s)])) as Record<FaceState, Readonly<Record<string, number>>>;
    for (let i = 0; i < FACE_STATES.length; i++) for (let j = i + 1; j < FACE_STATES.length; j++) {
      const a = FACE_STATES[i], b = FACE_STATES[j];
      assert.ok(dist(m[a], m[b]) > 1, `${temper}/${extra}: ${a} and ${b} look the same at their midpoints`);
    }
  }
});

test("the seed moves the blink, not the perch", () => {
  const look = DEFAULTS;
  // a blink lands at a different moment for a different seed…
  const at = (seed: number) => poseAt(look, "idle", 1300, seed).blink;   // steady blinks at 1300 + phase
  assert.ok(at(0) < 0.5, "seed 0 is mid-blink at 1300");
  // Some other bot is open-eyed at that same instant — that is the whole point
  // of the phase, and it must hold for ordinary neighbouring seeds, not just
  // one lucky number.
  const open = [1, 2, 3, 4, 5, 6, 7, 8].filter((s) => at(s) > 0.9);
  assert.ok(open.length >= 4, `expected most seeds open-eyed at 1300, got ${open.length}`);
  // …but the neutral perch is the same
  const a = poseAt(look, "idle", 0, 0), b = poseAt(look, "idle", 0, 3);
  for (const k of ["headTilt", "headDX", "bodyRot", "brow"]) assert.equal(a[k], b[k]);
});
