// Which group a thread sits in on a project chat's Overview. The order of the
// checks is the product: a thread you resolved stays resolved even while it
// idles; one blocked on you is "waiting" before anything else; work waiting
// to be merged is "ready" only once the thread has stopped.

import { test } from "node:test";
import assert from "node:assert/strict";
import { groupOf, stateLabel } from "../src/chat-overview.js";
import type { SessionView } from "../src/bridge.js";

const t = (over: Partial<SessionView>): SessionView => ({
  id: "x", title: "T", agentState: "idle", threadNumber: 1, ...over,
} as SessionView);

test("groups: resolved, waiting, working, ready, idle", () => {
  assert.equal(groupOf(t({ threadResolved: true, agentState: "permission" })), "resolved");
  assert.equal(groupOf(t({ agentState: "permission" })), "waiting");
  assert.equal(groupOf(t({ agentState: "waiting" })), "waiting");
  assert.equal(groupOf(t({ agentState: "working", threadUnmerged: 2 })), "working");
  assert.equal(groupOf(t({ agentState: "done", threadUnmerged: 2 })), "ready");
  assert.equal(groupOf(t({ agentState: "done", threadUnmerged: 0 })), "idle");
  assert.equal(groupOf(t({ agentState: "working", dormant: true })), "idle");
});

test("labels say what the group means", () => {
  assert.equal(stateLabel(t({ agentState: "done", threadUnmerged: 1 })), "1 commit to merge");
  assert.equal(stateLabel(t({ agentState: "done", threadUnmerged: 3 })), "3 commits to merge");
  assert.equal(stateLabel(t({ agentState: "permission" })), "Needs your permission");
  assert.equal(stateLabel(t({ threadResolved: true })), "Resolved");
});

// A message the project chat sent a thread must never read "From you". Long
// ones come back from the transcript wrapped as a paste — the case that did.
import { parsePrompt } from "../src/thread-ui.js";

test("the chat's message, even wrapped as a paste, is from the chat", () => {
  const raw = '<pasted_content id="6ffc">\n[Perch #7] From the project chat: You are still on **41 bids**.\n- pull more\n</pasted_content id="6ffc">';
  const p = parsePrompt(raw);
  assert.equal(p.from, "chat");
  assert.equal(p.text, "You are still on **41 bids**.\n- pull more");
  assert.equal(parsePrompt("[Perch #3] From the project chat: short").from, "chat");
});

test("your own steering is from you; the brief's kick-off is the brief", () => {
  assert.deepEqual(parsePrompt("[Perch #4] try the other table"), { from: "you", text: "try the other table" });
  assert.deepEqual(parsePrompt('<pasted_content id="a1">[Perch #5] a long note of mine</pasted_content id="a1">'), { from: "you", text: "a long note of mine" });
  assert.equal(parsePrompt("[Perch #1] Start on the task in your brief.").from, "brief");
});
