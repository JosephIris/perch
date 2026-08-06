// Unit tests for the pure layout math (layout.ts): the treeSignature rebuild
// gate and the drag-to-rearrange drop-zone picker. A wrong signature either
// remounts pane DOM needlessly (kills in-flight transitions, worst case reads
// as lost scrollback) or skips a rebuild the tree needed — both regressions
// this suite is meant to catch before they ship.

import { test } from "node:test";
import assert from "node:assert/strict";
import { treeSignature, computeEdge, isStageEntry } from "../src/layout.js";
import type { PaneTreeView } from "../src/bridge.js";

// Minimal tree builders. Only the fields treeSignature reads are meaningful;
// the rest are filled with inert defaults to satisfy the view type.
function leaf(paneId: string, extra: Partial<Extract<PaneTreeView, { kind: "leaf" }>> = {}): PaneTreeView {
  return {
    kind: "leaf",
    paneId,
    weight: 1,
    name: paneId,
    nameFull: "",
    url: null,
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
    ...extra,
  } as PaneTreeView;
}

function split(orientation: "h" | "v", children: PaneTreeView[], weight = 1): PaneTreeView {
  return { kind: "split", id: `split-${orientation}-${children.length}`, weight, orientation, children } as PaneTreeView;
}

test("treeSignature: same shape → same signature (focus/state churn must not rebuild)", () => {
  const a = split("v", [leaf("p1"), leaf("p2")]);
  const b = split("v", [
    leaf("p1", { agentState: "working", activityDetail: "Bash", commitCount: 3 }),
    leaf("p2", { name: "renamed", colorIndex: 4 }),
  ]);
  assert.equal(treeSignature(a), treeSignature(b));
});

test("treeSignature: weight changes never alter the signature (gutter drag must not rebuild)", () => {
  const before = split("v", [leaf("p1"), leaf("p2")]);
  const after = split("v", [
    { ...leaf("p1"), weight: 2.5 },
    { ...leaf("p2"), weight: 0.5 },
  ]);
  assert.equal(treeSignature(before), treeSignature(after));
});

test("treeSignature: shape changes DO alter it — new leaf, reorder, orientation, nesting", () => {
  const base = split("v", [leaf("p1"), leaf("p2")]);
  const added = split("v", [leaf("p1"), leaf("p2"), leaf("p3")]);
  const reordered = split("v", [leaf("p2"), leaf("p1")]);
  const rotated = split("h", [leaf("p1"), leaf("p2")]);
  const nested = split("v", [leaf("p1"), split("h", [leaf("p2"), leaf("p3")])]);

  const sigs = [base, added, reordered, rotated, nested].map(treeSignature);
  assert.equal(new Set(sigs).size, sigs.length);
});

test("treeSignature: url change on a leaf alters it (terminal pane ↔ URL pane rebuild)", () => {
  const term = leaf("p1");
  const web = leaf("p1", { url: "https://example.com" });
  assert.notEqual(treeSignature(term), treeSignature(web));
});

test("treeSignature: a board leaf is distinct from a terminal and from a URL pane", () => {
  // Each kind is a different class, so a leaf changing kind must remount.
  const term = leaf("p1");
  const web = leaf("p1", { url: "https://example.com" });
  const board = leaf("p1", { isBoard: true });
  const sigs = [term, web, board].map(treeSignature);
  assert.equal(new Set(sigs).size, 3);
});

test("treeSignature: isBoard=false reads the same as a plain terminal leaf", () => {
  // The host projects the flag on every leaf; an explicit false must not
  // gratuitously differ from an absent one, or every state push would rebuild.
  assert.equal(treeSignature(leaf("p1")), treeSignature(leaf("p1", { isBoard: false })));
});

test("computeEdge: centered box is 'center' (swap)", () => {
  assert.equal(computeEdge(0.5, 0.5), "center");
  assert.equal(computeEdge(0.3, 0.6), "center");   // still inside the ±0.22 box
});

test("computeEdge: nearest edge wins outside the center box", () => {
  assert.equal(computeEdge(0.05, 0.5), "left");
  assert.equal(computeEdge(0.95, 0.5), "right");
  assert.equal(computeEdge(0.5, 0.05), "top");
  assert.equal(computeEdge(0.5, 0.95), "bottom");
});

test("computeEdge: corner goes to the strictly closest edge", () => {
  assert.equal(computeEdge(0.1, 0.2), "left");     // 0.1 < 0.2 → left beats top
  assert.equal(computeEdge(0.2, 0.1), "top");      // 0.1 < 0.2 → top beats left
});

// ---- isStageEntry: the sleep → wake respawn -------------------------------
// A stage's panes stay mounted while hidden, so nothing re-reports their size
// on its own. Only an "entry" forces the refit, and only a pane.resize spawns
// a PTY — so getting this predicate wrong is precisely "the woken tab shows a
// dead prompt in the right folder and never resumes its agent".
//
// `legacyEntry` is the shipped-and-broken rule, kept here so the case it
// missed can't quietly come back.
const legacyEntry = (prev: string | null, next: string) =>
  prev !== null && prev !== next;

test("isStageEntry: waking a tab from the EMPTY workspace is an entry", () => {
  // Sleeping the last live tab in a project deactivates everything (prev =
  // null). Selecting the slept tab in the Idle drawer must refit → resize →
  // spawn → `claude --resume`.
  assert.equal(isStageEntry(null, "s1"), true);
  assert.equal(legacyEntry(null, "s1"), false);   // the bug, pinned
});

test("isStageEntry: waking a tab while another is on screen is an entry", () => {
  assert.equal(isStageEntry("s2", "s1"), true);
});

test("isStageEntry: a state push for the session already on screen is NOT an entry", () => {
  // This fires several times a second while agents work. Treating it as an
  // entry would refit + re-focus every push — stealing the caret and wiping
  // selections.
  assert.equal(isStageEntry("s1", "s1"), false);
});
