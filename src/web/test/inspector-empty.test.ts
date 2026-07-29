// The rail's empty state. Observed live: on a busy machine the inspector
// flickered "No agent in this pane" for a pane that plainly had one.
//
// Cause: a request that timed out resolved with a PAGE-SYNTHESIZED payload
// carrying hasAgent:false, which is indistinguishable from the host actually
// answering "this pane has no agent". Under load the host misses that deadline
// routinely, so the rail asserted something false, repeatedly.
//
// The fix adds a third state. These pin all three, because the bug was
// precisely that two of them collapsed into one.

import { test } from "node:test";
import assert from "node:assert/strict";
import { emptyState } from "../src/inspector.js";

test("a timed-out read says it is still reading, never 'no agent'", () => {
  const s = emptyState({ hasAgent: false, pending: true });
  assert.equal(s.title, "Reading…");
  assert.doesNotMatch(s.title, /no agent/i);
  assert.doesNotMatch(s.body, /no agent/i);
});

test("pending wins even if hasAgent happens to be true", () => {
  // A stale cached payload can carry hasAgent:true; not knowing still beats
  // claiming, so the pending flag decides.
  assert.equal(emptyState({ hasAgent: true, pending: true }).title, "Reading…");
});

test("a real reply with an agent but no rows says 'Nothing yet'", () => {
  assert.equal(emptyState({ hasAgent: true }).title, "Nothing yet");
});

test("only a real reply may claim the pane has no agent", () => {
  const s = emptyState({ hasAgent: false });
  assert.equal(s.title, "No agent in this pane");
  assert.match(s.body, /Start Claude here/);
});

test("pending:false is a real reply, not a pending one", () => {
  assert.equal(emptyState({ hasAgent: false, pending: false }).title, "No agent in this pane");
});

test("every state gives both a title and a body", () => {
  for (const d of [
    { hasAgent: false, pending: true },
    { hasAgent: true },
    { hasAgent: false },
  ]) {
    const s = emptyState(d);
    assert.ok(s.title.length > 0, "title must not be empty");
    assert.ok(s.body.length > 0, "body must not be empty");
  }
});
