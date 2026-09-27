// A project chat (and a thread) stays on its latest line while you're there,
// and "Go to latest" shows once you scroll away. The rule that broke it: a
// scroll that isn't yours — the tab re-attaching and resetting scrollTop to 0,
// a smooth scroll in flight — must never count as leaving the end.

import { test } from "node:test";
import assert from "node:assert/strict";
import { isAtEnd, nextPinned, END_SLACK } from "../src/stick-to-end.js";

test("at the end within the slack, not beyond it", () => {
  assert.equal(isAtEnd(1000, 600, 400), true);
  assert.equal(isAtEnd(1000, 600 - END_SLACK, 400), true);
  assert.equal(isAtEnd(1000, 600 - END_SLACK - 1, 400), false);
  assert.equal(isAtEnd(300, 0, 400), true, "content shorter than the view is at its end");
});

test("only your own scroll unpins", () => {
  assert.equal(nextPinned(true, false, true), false, "you scrolled up: stop following");
  assert.equal(nextPinned(true, false, false), true, "a re-attach reset to 0: keep following");
  assert.equal(nextPinned(false, false, false), false, "still away: stay away");
});

test("reaching the end pins again, however you got there", () => {
  assert.equal(nextPinned(false, true, true), true);
  assert.equal(nextPinned(false, true, false), true);
});
