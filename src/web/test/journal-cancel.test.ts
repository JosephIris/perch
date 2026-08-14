// Canceled-prompt detection + the reveal-pattern builder.
//
// canceledPrompts: a prompt is struck through ONLY when the very next event is
// the interrupt — the turn produced nothing, so cc's own chat quietly dropped
// it and the journal alone would still read as asked-and-answered. A turn that
// did work before the Esc was partially executed; striking it would claim it
// never ran.

import { test } from "node:test";
import assert from "node:assert/strict";
import { canceledPrompts, revealPatterns } from "../src/inspector.js";
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

// revealPatterns: the terminal shows a RENDERING (markdown marks dropped,
// wrapping, prefixes), so the pattern is a cleaned snippet with shorter
// fallbacks — longest first, so the most specific match wins.

test("patterns come longest-first with shorter fallbacks", () => {
  const long = "x".repeat(100);
  const got = revealPatterns(long);
  assert.deepEqual(got.map((n) => n.length), [80, 40, 24]);
});

test("markdown marks the terminal render drops are stripped from the pattern", () => {
  assert.equal(
    revealPatterns("## The **plan** is `simple` enough")[0],
    "The\\s+plan\\s+is\\s+simple\\s+enough");
});

test("a one-word opener is skipped for the first substantial line", () => {
  // Searching for "ok" would match half the buffer; the pattern has to be the
  // line with some meat on it.
  assert.equal(revealPatterns("ok\nThe real content is down here")[0],
    "The\\s+real\\s+content\\s+is\\s+down\\s+here");
});

// The one that makes the feature work at all. cc pads a wrapped row out to the
// terminal width before continuing on the next one, and xterm searches the two
// joined — so the text carries EXTRA spaces at the wrap. A literal needle for
// "…blue, and then…" misses; \s+ between the words finds it.
test("every space matches a run of whitespace, so a wrapped line still matches", () => {
  const [p] = revealPatterns("why the sky looks blue, and then one fact");
  const wrapped = "> why the sky looks blue,        and then one fact       ";
  assert.match(wrapped, new RegExp(p));
});

test("regex metacharacters in prose are escaped, not compiled", () => {
  const [p] = revealPatterns("run npm test (again) — cost $0 [really]");
  assert.match("$ run npm test (again) — cost $0 [really] now", new RegExp(p));
  assert.doesNotMatch("run npm test again — cost 0 really", new RegExp(p));
});

test("nothing searchable yields no patterns, not an empty-string search", () => {
  assert.deepEqual(revealPatterns("   "), []);
  assert.deepEqual(revealPatterns("## **`"), []);
});
