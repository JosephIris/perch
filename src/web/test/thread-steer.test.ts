// What the thread detail says about a steer that hasn't gone in yet. Before
// this, a line sent to a working thread vanished from the box and showed up
// nowhere until the turn ended — "writing to a thread doesn't work".

import { test } from "node:test";
import assert from "node:assert/strict";
import { queuedNote, queuedText } from "../src/thread-steer.js";
import type { SessionView } from "../src/bridge.js";

const t = (over: Partial<SessionView>): SessionView => ({
  id: "x", title: "T", agentState: "idle", threadNumber: 1, activityDetail: "", notification: null, ...over,
} as SessionView);

test("nothing queued, nothing shown", () => {
  assert.equal(queuedNote(undefined), null);
  assert.equal(queuedNote(t({})), null);
  assert.equal(queuedNote(t({ threadQueued: [] })), null);
  assert.equal(queuedNote(t({ threadQueued: ["  "] })), null);
});

test("a steer to a working thread says it waits for the turn, and how to send it now", () => {
  const n = queuedNote(t({ agentState: "working", threadQueued: ["use the v2 API"] }));
  assert.deepEqual(n?.lines, ["use the v2 API"]);
  assert.match(n!.when, /when it finishes this turn/);
  assert.match(n!.when, /Stop it to send now/);
  assert.match(queuedNote(t({ agentState: "working", threadQueued: ["a", "b"] }))!.when, /^These go/);
});

test("on a permission prompt it waits for the answer; asleep, for the wake; free, it is sending", () => {
  assert.match(queuedNote(t({ agentState: "permission", threadQueued: ["a"] }))!.when, /answer its permission/);
  assert.match(queuedNote(t({ agentState: "working", dormant: true, threadQueued: ["a"] }))!.when, /wakes up/);
  assert.equal(queuedNote(t({ agentState: "done", threadQueued: ["a"] }))!.when, "Sending…");
});

test("the project chat's own prefix is dropped from the bubble", () => {
  assert.equal(queuedText("From the project chat: rebase first"), "rebase first");
  assert.equal(queuedText("rebase first"), "rebase first");
});
