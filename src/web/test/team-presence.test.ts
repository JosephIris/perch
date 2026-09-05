// Presence is derived from the same `sessions[]` the sidebar renders, so the
// roster and the sidebar can never disagree about what a bot is doing. These
// pin the mapping, the roster order, the poll gate, the summary line, the
// room's three empty states, and which project the shortcut opens.

import { test } from "node:test";
import assert from "node:assert/strict";
import type { AgentStateName, SessionView, StateMessage, TeamBotView } from "../src/bridge.js";
import {
  presenceOf, rosterSort, anyWorking, teamSummary, roomEmptyState, teamProjectFor,
} from "../src/team.js";

function session(id: string, agentState: AgentStateName, extra: Partial<SessionView> = {}): SessionView {
  return {
    id, title: id, shell: "pwsh", projectId: "p1", worktreeBranch: "", boardPath: "",
    rootPane: { kind: "leaf", paneId: id + "-pane", name: id, agentState, agentType: "claude" } as SessionView["rootPane"],
    agentState, activityDetail: "", branch: "", ports: [], notification: null,
    paneCount: 1, waitingCount: 0, workingCount: 0,
    linesAdded: 0, linesDeleted: 0, filesChanged: 0, ahead: 0, aheadMine: 0,
    turnStartMs: 0, doneAtMs: 0, lastActivity: "",
    ...extra,
  };
}

function bot(nickname: string, sessionId: string): TeamBotView {
  return { botId: "b-" + nickname, nickname, positionSlug: "dev", positionName: "Dev", sessionId, peerName: nickname.toLowerCase() };
}

test("presence words", () => {
  assert.deepEqual(presenceOf(session("a", "working")), { state: "working", word: "working" });
  assert.deepEqual(presenceOf(session("a", "permission")), { state: "permission", word: "needs permission" });
  assert.deepEqual(presenceOf(session("a", "waiting")), { state: "waiting", word: "waiting for you" });
  assert.deepEqual(presenceOf(session("a", "done")), { state: "done", word: "idle" });
  assert.deepEqual(presenceOf(session("a", "idle")), { state: "idle", word: "idle" });
});

test("dormant wins over whatever the agent state says", () => {
  assert.deepEqual(presenceOf(session("a", "working", { dormant: true })), { state: "dormant", word: "asleep" });
});

test("no session at all is offline, not idle", () => {
  assert.deepEqual(presenceOf(undefined), { state: "offline", word: "not running" });
});

test("roster order: needs-you, working, resting, asleep, gone — stable within a rank", () => {
  const sessions = [
    session("s-ada", "done"),
    session("s-bo", "working"),
    session("s-cy", "permission"),
    session("s-di", "idle", { dormant: true }),
    session("s-ed", "working"),
  ];
  const bots = [bot("Ada", "s-ada"), bot("Bo", "s-bo"), bot("Cy", "s-cy"), bot("Di", "s-di"), bot("Ed", "s-ed"), bot("Fy", "")];
  assert.deepEqual(rosterSort(bots, sessions).map((b) => b.nickname), ["Cy", "Bo", "Ed", "Ada", "Di", "Fy"]);
});

test("anyWorking gates the poll", () => {
  const sessions = [session("s-ada", "done"), session("s-bo", "working")];
  assert.equal(anyWorking([bot("Ada", "s-ada")], sessions), false);
  assert.equal(anyWorking([bot("Ada", "s-ada"), bot("Bo", "s-bo")], sessions), true);
  assert.equal(anyWorking([bot("Bo", "s-bo")], [session("s-bo", "working", { dormant: true })]), false);
});

test("summary line leads with what needs you", () => {
  const sessions = [session("s-ada", "working"), session("s-bo", "working"), session("s-cy", "waiting"), session("s-di", "done")];
  const bots = [bot("Ada", "s-ada"), bot("Bo", "s-bo"), bot("Cy", "s-cy"), bot("Di", "s-di")];
  assert.equal(teamSummary(bots, sessions), "1 waiting · 2 working");
});

test("summary of a resting team says so, and an empty team says nothing", () => {
  const sessions = [session("s-ada", "done"), session("s-bo", "idle", { dormant: true })];
  assert.equal(teamSummary([bot("Ada", "s-ada"), bot("Bo", "s-bo"), bot("Cy", "")], sessions), "1 idle · 1 asleep · 1 not running");
  assert.equal(teamSummary([], sessions), "");
});

test("the room's three empty states stay distinct", () => {
  const ada = bot("Ada", "s-ada");
  assert.equal(roomEmptyState({ bots: [], entries: [] })?.title, "No bots yet");
  assert.equal(roomEmptyState({ bots: [ada], entries: [], pending: true })?.title, "Reading…");
  assert.equal(roomEmptyState({ bots: [ada], entries: [] })?.title, "Nothing yet");
  const one = { seq: 1, ts: "", kind: "beat" as const, from: "Ada", text: "hi" };
  assert.equal(roomEmptyState({ bots: [ada], entries: [one], pending: true }), null);
});

function state(activeSessionId: string, sessions: SessionView[], projects: StateMessage["projects"]): StateMessage {
  return {
    type: "state", activeSessionId, activePaneId: "", homeDir: "",
    prefs: {} as StateMessage["prefs"], modelLimits: [], projects, sessions, closedSessions: [],
  } as StateMessage;
}

test("the shortcut opens the active tab's project when it has bots", () => {
  const p1 = { id: "p1", name: "one", path: "c:/one", team: { bots: [bot("Ada", "s-ada")], positions: [] } };
  const p2 = { id: "p2", name: "two", path: "c:/two", team: { bots: [bot("Bo", "s-bo")], positions: [] } };
  const s = state("s-bo", [session("s-ada", "done"), session("s-bo", "done", { projectId: "p2" })], [p1, p2]);
  assert.equal(teamProjectFor(s), "p2");
});

test("… else the first project with bots, else nothing", () => {
  const bare = { id: "p0", name: "zero", path: "c:/zero" };
  const p1 = { id: "p1", name: "one", path: "c:/one", team: { bots: [bot("Ada", "s-ada")], positions: [] } };
  assert.equal(teamProjectFor(state("s-x", [session("s-x", "done", { projectId: "p0" })], [bare, p1])), "p1");
  assert.equal(teamProjectFor(state("s-x", [], [bare])), null);
});
