// Milestone B of the room: the decisions the page makes that need no DOM —
// what the feed shows and hides, how reactions group under a row, the task
// column's order, which cards are answered, the hand-off labels, and the link
// and image-path detectors the text renderer runs on every message.

import { test } from "node:test";
import assert from "node:assert/strict";
import { visibleEntries, reactionsFor, taskOrder, answeredSet, handoffLabel, REACTIONS } from "../src/team.js";
import { findLinks, findImagePaths, imageLabel } from "../src/text.js";
import { feedRowClass, systemTone, taskStatusWord, permissionDetails, cardKind, artefactKindWord, rowKey, rowSig } from "../src/team-room.js";
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

test("cardKind: the rows you answer wear their kind, narration wears nothing", () => {
  // Each of these gets a coloured frame in the CSS, keyed on the same event.
  assert.equal(cardKind("permission"), "permission");
  assert.equal(cardKind("ask"), "question");
  assert.equal(cardKind("trust"), "trust");
  assert.equal(cardKind("task.review"), "review");
  // Narration is not a card: a joined/left/reset row stays a quiet line.
  for (const e of ["joined", "left", "reset", "task", "cc", "delivered"] as const)
    assert.equal(cardKind(e), "", `${e} should not be framed`);
  assert.equal(cardKind(undefined), "");
});

test("artefactKindWord: says what the thing is, in words the owner uses", () => {
  assert.equal(artefactKindWord("md"), "document");
  assert.equal(artefactKindWord("csv"), "table");
  assert.equal(artefactKindWord("html"), "page");
  assert.equal(artefactKindWord("json"), "data");
  // An extension with no plain-English name is shown as itself, lowercased;
  // nothing is ever blank on the card.
  assert.equal(artefactKindWord("TS"), "ts");
  assert.equal(artefactKindWord(""), "note");
  assert.equal(artefactKindWord(undefined), "note");
});

test("rowSig: a row keeps its identity while nothing on it changed", () => {
  // This is what keeps a card's Allow button alive under the pointer. The feed
  // redraws whenever a bot does anything; a row whose signature is unchanged is
  // left in place, and a press that starts and ends on it still counts.
  const card = entry({ seq: 12, kind: "system", from: "perch", event: "permission", note: "p-9", text: "Bo wants to run Bash: git push" });
  const row = rowOf(card);
  const ctx = () => ({ answered: answeredSet([]), reactions: new Map(), optimistic: new Map(), sessions: new Map(), openFolds: new Set<number>(), openBeats: new Set<number>() });
  assert.equal(rowKey(row), "e12");
  assert.equal(rowSig(row, ctx()), rowSig(row, ctx()), "same row, same context, same signature");

  // Answered → the card must be rebuilt (its buttons are gone).
  const after = { ...ctx(), answered: answeredSet([entry({ seq: 13, kind: "system", from: "perch", event: "permission.answered", note: "p-9", text: "You allowed Bo" })]) };
  assert.notEqual(rowSig(row, after), rowSig(row, ctx()));

  // A reaction landing on the row changes it too.
  const reacted = { ...ctx(), reactions: new Map([[12, [{ emoji: "✅", who: ["you"] }]]]) };
  assert.notEqual(rowSig(row, reacted), rowSig(row, ctx()));

  // And an optimistic one you just clicked, before the host echoes it.
  const optimistic = { ...ctx(), optimistic: new Map([[12, new Set(["👀"])]]) };
  assert.notEqual(rowSig(row, optimistic), rowSig(row, ctx()));
});

test("rowSig: a folded run of tool calls is rebuilt only as it grows or opens", () => {
  const work = (seq: number) => entry({ seq, kind: "work", from: "Ada", verb: "Read", target: "a.ts", text: "" });
  const fold = (n: number): FeedRow =>
    ({ kind: "workfold", seq: 20, from: "Ada", entries: Array.from({ length: n }, (_, i) => work(20 + i)), summary: `read ${n} files`, cont: false });
  const ctx = () => ({ answered: answeredSet([]), reactions: new Map(), optimistic: new Map(), sessions: new Map(), openFolds: new Set<number>(), openBeats: new Set<number>() });
  assert.equal(rowKey(fold(2)), "w20");
  assert.equal(rowSig(fold(2), ctx()), rowSig(fold(2), ctx()));
  assert.notEqual(rowSig(fold(3), ctx()), rowSig(fold(2), ctx()));
  const open = { ...ctx(), openFolds: new Set([20]) };
  assert.notEqual(rowSig(fold(2), open), rowSig(fold(2), ctx()));
});

test("answeredSet: a card that ran out of time stops offering buttons", () => {
  // The hook waits about ten minutes, then Claude asks in the bot's own
  // terminal. Allow here would do nothing after that, so the card must close.
  const set = answeredSet([
    entry({ seq: 1, kind: "system", from: "perch", event: "permission", note: "p-1", text: "Bo wants to run Bash" }),
    entry({ seq: 2, kind: "system", from: "bo", event: "permission.expired", note: "p-1", text: "Bo waited ten minutes" }),
  ]);
  assert.ok(set.perms.has("p-1"));
  assert.equal(systemTone("permission.expired"), "attention");
});
