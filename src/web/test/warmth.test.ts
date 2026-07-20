// Age-warmth bucketing for a DONE row's elapsed label.
//
// `done` means the agent handed the turn back — every such row is YOUR move.
// A tall list of them is unscannable not because the state is wrong but
// because nothing encodes age: the one you dropped ten seconds ago looks
// exactly like the one parked since morning. warmthFor is that missing axis.
//
// The boundaries are the whole contract, so they're pinned from both sides.
// The other half of the contract is that the shared 1Hz ticker keeps
// data-warmth current WITHOUT re-rendering the sidebar — a bucket that only
// ever reflects render time would be wrong within two minutes of every row's
// life, which is precisely the window that matters most.

import { test } from "node:test";
import assert from "node:assert/strict";
import { warmthFor, fmtAge } from "../src/elapsed.js";

const SEC = 1000;
const MIN = 60 * SEC;
const HOUR = 60 * MIN;

test("warmth buckets, pinned on both sides of every boundary", () => {
  // hot: the turn just came back — you probably still have the context.
  assert.equal(warmthFor(0), "hot");
  assert.equal(warmthFor(12 * SEC), "hot");
  assert.equal(warmthFor(2 * MIN - 1), "hot");

  // warm: still this sitting, worth a glance.
  assert.equal(warmthFor(2 * MIN), "warm");
  assert.equal(warmthFor(6 * MIN), "warm");
  assert.equal(warmthFor(10 * MIN - 1), "warm");

  // cool: you've moved on to something else.
  assert.equal(warmthFor(10 * MIN), "cool");
  assert.equal(warmthFor(22 * MIN), "cool");
  assert.equal(warmthFor(HOUR - 1), "cool");

  // cold: parked. Still listed, no longer asking.
  assert.equal(warmthFor(HOUR), "cold");
  assert.equal(warmthFor(5 * HOUR), "cold");
});

test("a clock skew that runs the delta negative reads hot, not cold", () => {
  // doneAtMs is stamped host-side and compared against the webview's
  // Date.now(); a small skew can make `now - doneAt` negative. Clamping to
  // hot is the safe direction — a just-finished turn must never render as
  // parked-for-an-hour.
  assert.equal(warmthFor(-1), "hot");
  assert.equal(warmthFor(-5 * MIN), "hot");
});

test("age label is always ONE unit — the cold end has to be the quiet one", () => {
  assert.equal(fmtAge(0), "0s");
  assert.equal(fmtAge(12 * SEC), "12s");
  assert.equal(fmtAge(4 * MIN), "4m");
  assert.equal(fmtAge(59 * MIN), "59m");
  // The regression this pins: fmtElapsed renders these "2h 6m" / "5h 6m",
  // which put the noisiest string on the row exactly where the design wants
  // the quietest. Nobody triages a two-hour-old row on the trailing minutes.
  assert.equal(fmtAge(2 * HOUR + 6 * MIN), "2h");
  assert.equal(fmtAge(5 * HOUR + 6 * MIN), "5h");
  assert.equal(fmtAge(23 * HOUR), "23h");
  assert.equal(fmtAge(50 * HOUR), "2d");
});

test("age label never widens past 3 glyphs, so it can't crowd the title", () => {
  for (const ms of [0, 9 * SEC, 59 * SEC, MIN, 59 * MIN, HOUR, 23 * HOUR, 9 * 24 * HOUR]) {
    assert.ok(fmtAge(ms).length <= 3, `"${fmtAge(ms)}" is wider than the column allows`);
  }
});

test("buckets are ordered and total — every duration lands in exactly one", () => {
  const order = ["hot", "warm", "cool", "cold"];
  let last = 0;
  for (let m = 0; m <= 180; m++) {
    const idx = order.indexOf(warmthFor(m * MIN));
    assert.notEqual(idx, -1, `${m}m fell outside the ramp`);
    assert.ok(idx >= last, `warmth went backwards at ${m}m`);
    last = idx;
  }
  assert.equal(last, 3, "the ramp never reached cold");
});
