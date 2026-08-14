// Canceled-prompt detection + the reveal-needle builder.
//
// canceledPrompts: a prompt is struck through ONLY when the very next event is
// the interrupt — the turn produced nothing, so cc's own chat quietly dropped
// it and the journal alone would still read as asked-and-answered. A turn that
// did work before the Esc was partially executed; striking it would claim it
// never ran.

import { test } from "node:test";
import assert from "node:assert/strict";
import { canceledPrompts, revealNeedles } from "../src/inspector.js";
import type { InspectorEventView } from "../src/bridge.js";

const k = (kind: InspectorEventView["kind"]) => ({ kind });

test("a prompt directly followed by an interrupt is canceled", () => {
  const got = canceledPrompts([k("prompt"), k("interrupt")]);
  assert.deepEqual([...got], [0]);
});

test("a turn that did work before the Esc is NOT canceled", () => {
  // The agent read files / said something before you stopped it — that prompt
  // ran, partially. The interrupt row alone tells that story.
  assert.equal(canceledPrompts([k("prompt"), k("work"), k("interrupt")]).size, 0);
  assert.equal(canceledPrompts([k("prompt"), k("beat"), k("interrupt")]).size, 0);
});

test("only the adjacent prompt is struck, not every prompt above an interrupt", () => {
  const got = canceledPrompts([
    k("prompt"),              // answered fully
    k("beat"),
    k("prompt"),              // canceled
    k("interrupt"),
    k("prompt"),              // still open (tail of the transcript)
  ]);
  assert.deepEqual([...got], [2]);
});

test("cc's double interrupt marker cancels only the prompt touching it", () => {
  // Esc during tool use records TWO interrupt turns back to back. The second
  // interrupt must not implicate anything; only the prompt→interrupt edge does.
  const got = canceledPrompts([k("prompt"), k("interrupt"), k("interrupt")]);
  assert.deepEqual([...got], [0]);
});

test("a trailing prompt with no next event is an open turn, not a canceled one", () => {
  assert.equal(canceledPrompts([k("prompt")]).size, 0);
});

// revealNeedles: the terminal shows a RENDERING (markdown marks dropped,
// wrapping, prefixes), so the needle is a cleaned snippet with shorter
// fallbacks — longest first, so the most specific match wins.

test("needles come longest-first with shorter fallbacks", () => {
  const long = "x".repeat(100);
  const got = revealNeedles(long);
  assert.deepEqual(got.map((n) => n.length), [80, 40, 24]);
});

test("markdown marks the terminal render drops are stripped from the needle", () => {
  assert.equal(
    revealNeedles("## The **plan** is `simple` enough")[0],
    "The plan is simple enough");
});

test("a one-word opener is skipped for the first substantial line", () => {
  // Searching for "ok" would match half the buffer; the needle has to be the
  // line with some meat on it.
  assert.equal(revealNeedles("ok\nThe real content is down here")[0],
    "The real content is down here");
});

test("whitespace runs collapse — terminal wrapping re-spaces text", () => {
  assert.equal(revealNeedles("a  message   with\tgaps here")[0], "a message with gaps here");
});

test("nothing searchable yields no needles, not an empty-string search", () => {
  assert.deepEqual(revealNeedles("   "), []);
  assert.deepEqual(revealNeedles("## **`"), []);
});
