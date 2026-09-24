// A project's live tabs in three lanes: Chats (project chats and their
// threads), Sessions, Email (tabs opened from the inbox — spotted by their
// email pane). Order within a lane is kept; empty lanes are left out.

import { test } from "node:test";
import assert from "node:assert/strict";
import { splitLanes, isMailTab } from "../src/sidebar.js";
import type { SessionView, PaneTreeView } from "../src/bridge.js";

const leaf = (over: Record<string, unknown> = {}) => ({ kind: "leaf", paneId: "p", ...over } as unknown as PaneTreeView);
const s = (id: string, over: Partial<SessionView> = {}): SessionView =>
  ({ id, title: id, agentState: "idle", rootPane: leaf(), ...over } as SessionView);

test("an email tab is one with an email pane, anywhere in its split", () => {
  assert.equal(isMailTab(s("a", { rootPane: leaf({ mailId: "18f2" }) })), true);
  const split = { kind: "split", id: "x", orientation: "h", children: [leaf(), leaf({ mailId: "18f2" })] } as unknown as PaneTreeView;
  assert.equal(isMailTab(s("b", { rootPane: split })), true);
  assert.equal(isMailTab(s("c")), false);
});

test("three lanes, in order, each keeping its tabs' order", () => {
  const live = [
    s("chat", { isLead: true }), s("t1", { threadOf: "chat" }),
    s("work1"), s("mail1", { rootPane: leaf({ mailId: "m" }) }), s("work2"),
  ];
  const lanes = splitLanes(live);
  assert.deepEqual(lanes.map((l) => [l.id, l.items.map((x) => x.id), l.count]), [
    ["chats", ["chat", "t1"], 1],
    ["sessions", ["work1", "work2"], 2],
    ["email", ["mail1"], 1],
  ]);
});

test("empty lanes are left out; a chat's email-pane thread stays a chat", () => {
  assert.deepEqual(splitLanes([s("a"), s("b")]).map((l) => l.id), ["sessions"]);
  assert.deepEqual(splitLanes([s("t", { threadOf: "c", rootPane: leaf({ mailId: "m" }) })]).map((l) => l.id), ["chats"]);
  assert.deepEqual(splitLanes([]), []);
});
