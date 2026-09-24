// A project chat's threads fold under it in the sidebar: shut by default,
// resolved ones behind a drawer of their own — but folding never hides a
// thread that needs you, or the one on screen.

import { test } from "node:test";
import assert from "node:assert/strict";
import { visibleThreads } from "../src/sidebar.js";
import type { SessionView } from "../src/bridge.js";

const s = (id: string, over: Partial<SessionView> = {}): SessionView =>
  ({ id, title: id, agentState: "idle", threadOf: "chat", ...over } as SessionView);

const kids = [
  s("working", { agentState: "working" }),
  s("asking", { agentState: "permission" }),
  s("done"),
  s("old", { threadResolved: true }),
];

test("shut: only a thread that needs you, or the one on screen", () => {
  const v = visibleThreads(kids, "done", false, false);
  assert.deepEqual(v.rows.map((x) => x.id), ["asking", "done"]);
  assert.deepEqual(v.resolved, []);
  assert.equal(v.open.length, 3);
});

test("open: every open thread; resolved ones only with their drawer open", () => {
  assert.deepEqual(visibleThreads(kids, "", true, false).rows.map((x) => x.id), ["working", "asking", "done"]);
  assert.deepEqual(visibleThreads(kids, "", true, false).resolved, []);
  assert.deepEqual(visibleThreads(kids, "", true, true).resolved.map((x) => x.id), ["old"]);
});

test("a resolved thread on screen stays in view; all-resolved opens straight to them", () => {
  assert.deepEqual(visibleThreads(kids, "old", false, false).resolved.map((x) => x.id), ["old"]);
  const allDone = [s("a", { threadResolved: true }), s("b", { threadResolved: true })];
  assert.deepEqual(visibleThreads(allDone, "", true, false).resolved.map((x) => x.id), ["a", "b"]);
});

test("a slept thread waiting on nothing folds away", () => {
  assert.deepEqual(visibleThreads([s("z", { agentState: "permission", dormant: true })], "", false, false).rows, []);
});
