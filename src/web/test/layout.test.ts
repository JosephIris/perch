// Unit tests for the pure layout math (layout.ts): the treeSignature rebuild
// gate and the drag-to-rearrange drop-zone picker. A wrong signature either
// remounts pane DOM needlessly (kills in-flight transitions, worst case reads
// as lost scrollback) or skips a rebuild the tree needed — both regressions
// this suite is meant to catch before they ship.

import { test } from "node:test";
import assert from "node:assert/strict";
import { treeSignature, computeEdge } from "../src/layout.js";
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
