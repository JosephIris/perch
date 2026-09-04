// The room's feed rules: merging pushes with polls by seq, folding a bot's
// tool calls into one quiet line, chat-style continuation, and what counts as
// unread. All pure — the view only ever renders what these decide.

import { test } from "node:test";
import assert from "node:assert/strict";
import type { TeamEntryView } from "../src/bridge.js";
import { mergeEntries, foldFeed, summarizeWork, groupRows, unreadCount } from "../src/team.js";

const T0 = Date.parse("2026-09-02T10:00:00Z");
function at(offsetSec: number): string { return new Date(T0 + offsetSec * 1000).toISOString(); }

function entry(seq: number, kind: TeamEntryView["kind"], from: string, extra: Partial<TeamEntryView> = {}): TeamEntryView {
  return { seq, ts: at(seq), kind, from, text: `${kind} ${seq}`, ...extra };
}
function work(seq: number, from: string, verb: string, target: string, repeat = 1): TeamEntryView {
  return entry(seq, "work", from, { verb, target, repeat, text: "" });
}

test("merge unions by seq, sorted, incoming replacing a held row", () => {
  const held = [entry(1, "user", "you", { delivered: false }), entry(3, "beat", "Ada")];
  const incoming = [entry(2, "system", "perch"), entry(1, "user", "you", { delivered: true })];
  const merged = mergeEntries(held, incoming);
  assert.deepEqual(merged.map((e) => e.seq), [1, 2, 3]);
  assert.equal(merged[0].delivered, true);
});

test("merge into nothing keeps order by seq, not arrival", () => {
  const merged = mergeEntries([], [entry(5, "beat", "Ada"), entry(4, "beat", "Bo")]);
  assert.deepEqual(merged.map((e) => e.seq), [4, 5]);
});

test("consecutive work from one bot folds into one row", () => {
  const rows = foldFeed([
    entry(1, "beat", "Ada"),
    work(2, "Ada", "Read", "a.ts"),
    work(3, "Ada", "Edit", "a.ts"),
    entry(4, "beat", "Ada"),
  ]);
  assert.deepEqual(rows.map((r) => r.kind), ["entry", "workfold", "entry"]);
  const fold = rows[1];
  assert.equal(fold.kind, "workfold");
  if (fold.kind === "workfold") {
    assert.equal(fold.seq, 2);
    assert.equal(fold.from, "Ada");
    assert.equal(fold.entries.length, 2);
  }
});

test("another bot's work ends the fold; so does a system line", () => {
  const rows = foldFeed([
    work(1, "Ada", "Read", "a.ts"),
    work(2, "Bo", "Read", "b.ts"),
    entry(3, "system", "perch", { event: "joined" }),
    work(4, "Bo", "Read", "c.ts"),
  ]);
  assert.deepEqual(rows.map((r) => r.kind), ["workfold", "workfold", "entry", "workfold"]);
});

test("the fold carries the bot's ids from its first entry", () => {
  const rows = foldFeed([work(1, "Ada", "Read", "a.ts", 1)].map((e) => ({ ...e, botId: "b1", paneId: "p1" })));
  const fold = rows[0];
  assert.equal(fold.kind, "workfold");
  if (fold.kind === "workfold") {
    assert.equal(fold.botId, "b1");
    assert.equal(fold.paneId, "p1");
  }
});

test("work summary counts distinct files for edits and reads, calls for commands", () => {
  const s = summarizeWork([
    work(1, "Ada", "Edit", "a.ts"),
    work(2, "Ada", "Write", "b.ts"),
    work(3, "Ada", "Edit", "a.ts"),
    work(4, "Ada", "Read", "perch.log", 6),
    work(5, "Ada", "Bash", "npm test", 2),
    work(6, "Ada", "Skill", "run"),
    work(7, "Ada", "Agent", "explore"),
  ]);
  assert.equal(s, "edited 2 files · read 1 file · ran 2 commands · used 1 skill · 1 other step");
});

test("work summary of nothing recognisable still says something", () => {
  assert.equal(summarizeWork([]), "worked");
});

test("a message within three minutes from the same author continues", () => {
  const rows = groupRows(foldFeed([
    entry(1, "beat", "Ada"),
    entry(2, "beat", "Ada"),
    entry(3, "beat", "Bo"),
  ]));
  assert.deepEqual(rows.map((r) => r.cont), [false, true, false]);
});

test("a gap longer than three minutes starts a new header", () => {
  const a = entry(1, "beat", "Ada", { ts: at(0) });
  const b = entry(2, "beat", "Ada", { ts: at(3 * 60 + 1) });
  assert.deepEqual(groupRows(foldFeed([a, b])).map((r) => r.cont), [false, false]);
});

test("a work fold or a system line breaks continuation", () => {
  const rows = groupRows(foldFeed([
    entry(1, "beat", "Ada"),
    work(2, "Ada", "Read", "a.ts"),
    entry(3, "beat", "Ada"),
    entry(4, "system", "perch", { event: "waiting" }),
    entry(5, "beat", "Ada"),
  ]));
  assert.deepEqual(rows.map((r) => r.cont), [false, false, false, false, false]);
});

test("a peer message to a different bot is a new header", () => {
  const rows = groupRows(foldFeed([
    entry(1, "peer", "Ada", { to: ["Bo"] }),
    entry(2, "peer", "Ada", { to: ["Bo"] }),
    entry(3, "peer", "Ada", { to: ["Cy"] }),
  ]));
  assert.deepEqual(rows.map((r) => r.cont), [false, true, false]);
});

test("groupRows does not mutate its input", () => {
  const rows = foldFeed([entry(1, "beat", "Ada"), entry(2, "beat", "Ada")]);
  groupRows(rows);
  assert.equal(rows[1].cont, false);
});

test("unread counts messages after the watermark, never work or your own", () => {
  const entries = [
    entry(1, "beat", "Ada"),
    entry(2, "user", "you"),
    work(3, "Ada", "Read", "a.ts"),
    entry(4, "peer", "Ada", { to: ["Bo"] }),
    entry(5, "system", "perch", { event: "joined" }),
  ];
  assert.equal(unreadCount(entries, 0), 3);
  assert.equal(unreadCount(entries, 1), 2);
  assert.equal(unreadCount(entries, 5), 0);
});

test("foldFeed: two or more board changes in a row fold into one line; one stays a row", () => {
  const change = (seq: number, text: string) => entry(seq, "system", "perch", { event: "task", text });
  const rows = foldFeed([
    entry(1, "beat", "Ada", { text: "hi" }),
    change(2, "Ada: doing — faces"),
    change(3, "Lee gave Bo: the API"),
    change(4, "Bo: done — the API"),
    entry(5, "system", "perch", { event: "joined", text: "Cy joined" }),
    change(6, "Task set by Joseph: next"),
    entry(7, "beat", "Bo", { text: "ok" }),
  ]);
  assert.deepEqual(rows.map((r) => r.kind), ["entry", "sysfold", "entry", "entry", "entry"]);
  const fold = rows[1];
  if (fold.kind === "sysfold") {
    assert.equal(fold.seq, 2);
    assert.equal(fold.entries.length, 3);
    assert.equal(fold.summary, "Board updated · 3 changes");
  }
  // The lone change after the join is a plain row, not a fold of one.
  assert.equal(rows[3].kind, "entry");
});
