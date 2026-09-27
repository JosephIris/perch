// The dashboard's cards: one per project, a chat's threads rolled up under
// it, the most urgent project first, and nothing that needs you ever hidden.

import { test } from "node:test";
import assert from "node:assert/strict";
import { buildBoard } from "../src/dashboard-model.js";
import type { SessionView, ProjectView, PaneTreeView } from "../src/bridge.js";

const leaf = (mailId?: string): PaneTreeView => ({ kind: "leaf", paneId: "p", name: "", agentState: "idle", mailId } as PaneTreeView);
const s = (over: Partial<SessionView> & { id: string }): SessionView => ({
  title: over.id, agentState: "done", ahead: 0, rootPane: leaf(), ...over,
} as SessionView);
const projects: ProjectView[] = [
  { id: "a", name: "alpha", path: "" },
  { id: "b", name: "beta", path: "" },
  { id: "c", name: "gamma", path: "", hidden: true },
  { id: "d", name: "delta", path: "" },
];

test("a chat rolls up its threads; what needs you is counted and named first", () => {
  const { cards } = buildBoard([
    s({ id: "chat", projectId: "a", isLead: true }),
    s({ id: "t1", projectId: "a", threadOf: "chat", threadNumber: 1, agentState: "working" }),
    s({ id: "t2", projectId: "a", threadOf: "chat", threadNumber: 2, agentState: "done", threadUnmerged: 3 }),
    s({ id: "t3", projectId: "a", threadOf: "chat", threadNumber: 3, agentState: "permission" }),
    s({ id: "t4", projectId: "a", threadOf: "chat", threadNumber: 4, threadResolved: true }),
  ], projects);
  const a = cards[0];
  assert.equal(a.name, "alpha");
  assert.equal(a.chats.length, 1);
  assert.equal(a.sessions.length, 0, "threads are not listed as sessions");
  assert.deepEqual(a.chats[0].counts, { waiting: 1, working: 1, ready: 1, idle: 0 });
  assert.deepEqual(a.chats[0].attention.map((t) => t.id), ["t3", "t2"]);
  assert.deepEqual([a.needs, a.working, a.ready], [1, 1, 1]);
});

test("sessions and email tabs are filed apart; asleep tabs are only counted", () => {
  const { cards } = buildBoard([
    s({ id: "work", projectId: "b", ahead: 2 }),
    s({ id: "mail", projectId: "b", rootPane: leaf("m1") }),
    s({ id: "zz", projectId: "b", dormant: true }),
  ], projects);
  const b = cards[0];
  assert.deepEqual(b.sessions.map((x) => x.id), ["work"]);
  assert.deepEqual(b.email.map((x) => x.id), ["mail"]);
  assert.equal(b.asleep, 1);
  assert.equal(b.ready, 1, "commits to push count as ready");
});

test("most urgent project first; hidden shows only when it needs you; empty ones are counted", () => {
  const { cards, quiet } = buildBoard([
    s({ id: "x", projectId: "a" }),
    s({ id: "y", projectId: "b", agentState: "working" }),
    s({ id: "z", projectId: "c", agentState: "waiting" }),
    s({ id: "o", agentState: "done" }),
  ], projects);
  assert.deepEqual(cards.map((c) => c.name), ["gamma", "beta", "alpha", "Other"]);
  assert.equal(quiet, 1, "delta has nothing open");
  const calm = buildBoard([s({ id: "z", projectId: "c" })], projects);
  assert.equal(calm.cards.length, 0, "a hidden project at rest stays hidden");
});
