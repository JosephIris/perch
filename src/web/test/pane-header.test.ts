// Unit tests for the pane header's ports chip (applyPorts). The dev-server
// port(s) a pane owns render as a neutral, clickable "⊙ :port" chip on the
// header identity strip (promoted from the footer). applyPorts only mutates the
// element it's handed, so we exercise it against a tiny fake — no DOM shim.

import { test } from "node:test";
import assert from "node:assert/strict";
import { applyPorts } from "../src/pane-header.js";

type Leaf = Parameters<typeof applyPorts>[1];

function leaf(over: Partial<Leaf> = {}): Leaf {
  // Only the fields applyPorts reads matter; the rest satisfy the type.
  return {
    kind: "leaf", paneId: "p1", name: "pane", colorIndex: 0,
    agentState: "idle", activityDetail: "", branch: "", ports: [],
    notification: null, commitCount: 0, linesAdded: 0, linesDeleted: 0,
    filesChanged: 0, ahead: 0, turnStartMs: 0, doneAtMs: 0,
    ...over,
  } as Leaf;
}

/** Minimal stand-in for the ports <button> — just the props applyPorts touches. */
function fakeChip() {
  return { style: { display: "" }, dataset: {} as Record<string, string>, textContent: "", title: "" };
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const apply = (el: ReturnType<typeof fakeChip>, l: Leaf) => applyPorts(el as any, l);

test("no ports → chip hidden and cleared", () => {
  const el = fakeChip();
  apply(el, leaf({ ports: [] }));
  assert.equal(el.style.display, "none");
  assert.equal(el.textContent, "");
  assert.equal(el.dataset.port, "");
});

test("one port → visible '⊙ :port', no '+N', dataset carries the port", () => {
  const el = fakeChip();
  apply(el, leaf({ ports: [5173] }));
  assert.equal(el.style.display, "");
  assert.match(el.textContent, /:5173/);
  assert.doesNotMatch(el.textContent, /\+/);
  assert.equal(el.dataset.port, "5173");
  assert.match(el.title, /5173/);
});

test("multiple ports → primary shown with ' +N', click target is the primary", () => {
  const el = fakeChip();
  apply(el, leaf({ ports: [8000, 8001, 8002] }));
  assert.match(el.textContent, /:8000 \+2/);
  assert.equal(el.dataset.port, "8000");   // click opens the first
});

test("clears back to hidden when a pane's ports go away", () => {
  const el = fakeChip();
  apply(el, leaf({ ports: [3000] }));
  apply(el, leaf({ ports: [] }));
  assert.equal(el.style.display, "none");
  assert.equal(el.dataset.port, "");
});
