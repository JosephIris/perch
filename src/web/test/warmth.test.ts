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
import { warmthFor } from "../src/elapsed.js";

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
