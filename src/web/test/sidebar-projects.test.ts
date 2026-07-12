// Project-mode grouping.
//
// `other` (sessions not filed under any project) is computed but NOT rendered in
// project mode: a scratch shell in your home directory isn't a project, and
// bucketing it under the projects made that view read as a second, worse copy of
// the session list. Sessions mode is where an unfiled session lives.
//
// It still matters that `other` catches them rather than dropping them on the
// floor — a tab whose project was unregistered mid-run must land somewhere
// definite, not match no group at all. These pin the partition itself.

import { test } from "node:test";
import assert from "node:assert/strict";
import { groupByProject, aggregateState } from "../src/sidebar.js";
import type { SessionView, ProjectView, AgentStateName } from "../src/bridge.js";

function sess(id: string, projectId = "", agentState: AgentStateName = "idle"): SessionView {
  return {
    id,
    title: id,
    shell: "pwsh",
    projectId,
    worktreeBranch: "",
    rootPane: {
      kind: "leaf",
      paneId: `pane-${id}`,
      name: id,
      colorIndex: 0,
      agentState: "idle",
      agentType: "",
      activityDetail: "",
      branch: "",
      ports: [],
      commitCount: 0,
      linesAdded: 0,
      linesDeleted: 0,
      filesChanged: 0,
      ahead: 0,
      turnStartMs: 0,
      doneAtMs: 0,
      notification: null,
    },
    agentState,
    activityDetail: "",
    branch: "",
    ports: [],
    notification: null,
    paneCount: 1,
    waitingCount: 0,
    workingCount: 0,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    lastActivity: "now",
  };
}

const proj = (id: string, name: string): ProjectView => ({ id, name, path: `C:\\src\\${name}` });

test("files tabs under their project, keeping registration order", () => {
  const a = proj("p1", "cmux-win");
  const b = proj("p2", "global-models");
  const { groups, other } = groupByProject(
    [sess("s1", "p2"), sess("s2", "p1"), sess("s3", "p1")],
    [a, b]
  );

  assert.deepEqual(groups.map((g) => g.project.id), ["p1", "p2"]);
  assert.deepEqual(groups[0].tabs.map((t) => t.id), ["s2", "s3"]);
  assert.deepEqual(groups[1].tabs.map((t) => t.id), ["s1"]);
  assert.deepEqual(other, []);
});

test("a project with no tabs still gets a group, so its + stays reachable", () => {
  const { groups } = groupByProject([], [proj("p1", "cmux-win")]);
  assert.equal(groups.length, 1);
  assert.deepEqual(groups[0].tabs, []);
});

test("an unfiled session is never mistaken for a project's tab", () => {
  const { groups, other } = groupByProject([sess("s1"), sess("s2", "p1")], [proj("p1", "cmux-win")]);
  assert.deepEqual(groups[0].tabs.map((t) => t.id), ["s2"]);
  assert.deepEqual(other.map((s) => s.id), ["s1"]);
});

test("a tab whose project was unregistered is partitioned out, not into the void", () => {
  // The tab still claims p-gone; no such project is registered any more. If the
  // grouping only matched known ids and only swept `!projectId` aside, this
  // running session would belong to neither set — a silent hole in the
  // partition. Every session must land in exactly one of groups | other.
  const { groups, other } = groupByProject(
    [sess("s1", "p-gone"), sess("s2", "p1")],
    [proj("p1", "cmux-win")]
  );
  assert.deepEqual(other.map((s) => s.id), ["s1"]);
  assert.deepEqual(groups[0].tabs.map((t) => t.id), ["s2"]);
});

test("groups | other is a total partition — no session is dropped", () => {
  const all = [sess("s1"), sess("s2", "p1"), sess("s3", "p-gone"), sess("s4", "p1")];
  const { groups, other } = groupByProject(all, [proj("p1", "cmux-win")]);
  const seen = [...groups.flatMap((g) => g.tabs), ...other].map((s) => s.id).sort();
  assert.deepEqual(seen, ["s1", "s2", "s3", "s4"]);
});

test("no projects at all → nothing is grouped", () => {
  const { groups, other } = groupByProject([sess("s1"), sess("s2", "p1")], []);
  assert.deepEqual(groups, []);
  assert.deepEqual(other.map((s) => s.id), ["s1", "s2"]);
});

// The dot on a COLLAPSED project header. Folding a group away must not be able
// to hide an agent that's blocked on you, so the header wears its tabs'
// most-urgent state.
test("collapsed header state: a blocked agent outranks everything else", () => {
  assert.equal(
    aggregateState([sess("a", "p", "working"), sess("b", "p", "permission"), sess("c", "p", "done")]),
    "permission"
  );
});

test("collapsed header state: waiting outranks done and working", () => {
  assert.equal(aggregateState([sess("a", "p", "done"), sess("b", "p", "waiting")]), "waiting");
  assert.equal(aggregateState([sess("a", "p", "working"), sess("b", "p", "done")]), "done");
});

test("collapsed header state: all-idle (and empty) stays idle, so no dot shows", () => {
  assert.equal(aggregateState([sess("a", "p"), sess("b", "p")]), "idle");
  assert.equal(aggregateState([]), "idle");
});

// The header dot is what makes "collapsed means collapsed" safe. Folding a group
// now hides EVERY tab in it (keeping the active one visible under a shut chevron
// made the control contradict itself) — so the only thing standing between a
// blocked agent and total invisibility is this dot.
test("collapsed header state: a single blocked tab still raises the dot", () => {
  assert.equal(aggregateState([sess("only", "p", "permission")]), "permission");
});
