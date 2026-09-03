// Milestone B of the room: the decisions the page makes that need no DOM —
// what the feed shows and hides, how reactions group under a row, the task
// column's order, which cards are answered, the hand-off labels, and the link
// and image-path detectors the text renderer runs on every message.

import { test } from "node:test";
import assert from "node:assert/strict";
import { visibleEntries, reactionsFor, taskOrder, answeredSet, handoffLabel, REACTIONS } from "../src/team.js";
import { findLinks, findImagePaths, imageLabel } from "../src/text.js";
import { feedRowClass, systemTone, taskStatusWord, permissionDetails } from "../src/team-room.js";
import type { TeamEntryView, TeamTaskView } from "../src/bridge.js";
import type { FeedRow } from "../src/team.js";

const entry = (over: Partial<TeamEntryView>): TeamEntryView =>
  ({ seq: 1, ts: "2026-09-03T10:00:00Z", kind: "beat", from: "Ada", text: "hi", ...over });
const rowOf = (e: TeamEntryView, cont = false): FeedRow => ({ kind: "entry", seq: e.seq, entry: e, cont });

test("visibleEntries: reactions never render as rows, deliveries stay hidden, activity only on request", () => {
  const rows = [
    entry({ seq: 1, kind: "user", from: "you" }),
    entry({ seq: 2, kind: "work", verb: "Read", target: "a.ts" }),
    entry({ seq: 3, kind: "reaction", from: "Ada", text: "✅", note: "1" }),
    entry({ seq: 4, kind: "system", from: "perch", event: "delivered", text: "Delivered to Ada" }),
    entry({ seq: 5, kind: "beat" }),
  ];
  assert.deepEqual(visibleEntries(rows, false).map((e) => e.seq), [1, 5]);
  assert.deepEqual(visibleEntries(rows, true).map((e) => e.seq), [1, 2, 5]);
});

test("reactionsFor: grouped by target row, each emoji once, senders in order, junk ignored", () => {
  const rows = [
    entry({ seq: 10, kind: "reaction", from: "Ada", text: "✅", note: "3" }),
    entry({ seq: 11, kind: "reaction", from: "you", text: "✅", note: "3" }),
    entry({ seq: 12, kind: "reaction", from: "Ada", text: "✅", note: "3" }),     // host dedupes; so do we
    entry({ seq: 13, kind: "reaction", from: "Bo", text: "👀", note: "3" }),
    entry({ seq: 14, kind: "reaction", from: "Bo", text: "👋", note: "7" }),
    entry({ seq: 15, kind: "reaction", from: "Bo", text: "", note: "7" }),        // no emoji
    entry({ seq: 16, kind: "reaction", from: "Bo", text: "👋", note: "nope" }),   // no target
    entry({ seq: 17, kind: "beat", text: "not a reaction", note: "3" }),
  ];
  const m = reactionsFor(rows);
  assert.deepEqual([...m.keys()], [3, 7]);
  assert.deepEqual(m.get(3), [{ emoji: "✅", who: ["Ada", "you"] }, { emoji: "👀", who: ["Bo"] }]);
  assert.deepEqual(m.get(7), [{ emoji: "👋", who: ["Bo"] }]);
  assert.deepEqual([...REACTIONS], ["✅", "👀", "✏️", "👋"]);
});

test("taskOrder: what needs you first, then what's moving, then what's wrapping up; stable within", () => {
  const t = (id: string, status: string): TeamTaskView =>
    ({ id, title: id, status, setBy: "you", createdAtMs: 0, items: [], wrapping: [] });
  const order = taskOrder([t("a", "open"), t("b", "done"), t("c", "review"), t("d", "open"), t("e", "review")]);
  assert.deepEqual(order.map((x) => x.id), ["c", "e", "a", "d", "b"]);
});

test("answeredSet: permission and ask cards by id, the start-up question by nickname", () => {
  const rows = [
    entry({ seq: 1, kind: "system", from: "perch", event: "permission", note: "p1", to: ["Bo"] }),
    entry({ seq: 2, kind: "system", from: "perch", event: "permission.answered", note: "p1" }),
    entry({ seq: 3, kind: "system", from: "perch", event: "ask", note: "a1", to: ["Cy"] }),
    entry({ seq: 4, kind: "system", from: "perch", event: "trusted", to: ["Ada"] }),
    entry({ seq: 5, kind: "system", from: "perch", event: "ask", note: "a2", to: ["Cy"] }),
    entry({ seq: 6, kind: "system", from: "perch", event: "ask.answered", note: "a2" }),
  ];
  const a = answeredSet(rows);
  assert.deepEqual([...a.perms], ["p1"]);
  assert.deepEqual([...a.asks], ["a2"]);
  assert.deepEqual([...a.trust], ["Ada"]);
});

test("handoffLabel: the five prefixes, case-insensitive, nothing else", () => {
  assert.equal(handoffLabel("handoff"), "hand-off");
  assert.equal(handoffLabel("REPORT"), "report");
  assert.equal(handoffLabel("question"), "question");
  assert.equal(handoffLabel("Answer"), "answer");
  assert.equal(handoffLabel("fyi"), "fyi");
  assert.equal(handoffLabel("summary"), null);
  assert.equal(handoffLabel(undefined), null);
});

test("feedRowClass: a peer question wears the question class", () => {
  assert.equal(feedRowClass(rowOf(entry({ kind: "peer", note: "question" }))), "tf-msg tf-msg--peer tf-msg--question");
  assert.equal(feedRowClass(rowOf(entry({ kind: "peer", note: "report" }))), "tf-msg tf-msg--peer");
});

test("systemTone: the cards you answer are attention, a classifier block is an error", () => {
  assert.equal(systemTone("permission"), "attention");
  assert.equal(systemTone("ask"), "attention");
  assert.equal(systemTone("trust"), "attention");
  assert.equal(systemTone("task.review"), "attention");
  assert.equal(systemTone("denied"), "error");
  assert.equal(systemTone("permission.answered"), "calm");
  assert.equal(systemTone("ask.answered"), "calm");
  assert.equal(systemTone("cc"), "calm");
});

test("taskStatusWord: every status has a word, unknown none", () => {
  assert.equal(taskStatusWord("open"), "in progress");
  assert.equal(taskStatusWord("review"), "confirm?");
  assert.equal(taskStatusWord("done"), "wrapping up");
  assert.equal(taskStatusWord("archived"), "");
  assert.equal(taskStatusWord(undefined), "");
});

test("permissionDetails: prettified JSON, capped at eight lines, raw text when not JSON", () => {
  const pretty = permissionDetails(JSON.stringify({ command: "git push", timeout: 5 }));
  assert.equal(pretty, "{\n  \"command\": \"git push\",\n  \"timeout\": 5\n}");
  const big = permissionDetails(JSON.stringify({ a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8, i: 9 }));
  assert.equal(big.split("\n").length, 8);
  assert.ok(big.endsWith("…"));
  assert.equal(permissionDetails("rm -rf build"), "rm -rf build");
  assert.equal(permissionDetails(undefined), "");
});

test("findLinks: http(s) URLs, trailing punctuation left to the sentence, balanced parens kept", () => {
  const text = "See https://example.com/a?b=1. Then (https://en.wikipedia.org/wiki/Foo_(bar)) and http://localhost:5103/harness#kpi, ok";
  const links = findLinks(text).map((s) => s.value);
  assert.deepEqual(links, ["https://example.com/a?b=1", "https://en.wikipedia.org/wiki/Foo_(bar)", "http://localhost:5103/harness#kpi"]);
  assert.deepEqual(findLinks("no links here, http:// alone doesn't count"), []);
});

test("findImagePaths: absolute Windows or rooted paths ending in an image extension", () => {
  const text = "Shots: C:\\dev\\app\\design-loop\\kpi-dark.png and /tmp/out/final.JPG, not C:\\dev\\notes.md nor relative/x.png";
  const paths = findImagePaths(text).map((s) => s.value);
  assert.deepEqual(paths, ["C:\\dev\\app\\design-loop\\kpi-dark.png", "/tmp/out/final.JPG"]);
  assert.equal(imageLabel("C:\\dev\\app\\design-loop\\kpi-dark.png"), "kpi-dark.png");
  assert.equal(imageLabel("/tmp/out/final.JPG"), "final.JPG");
});
