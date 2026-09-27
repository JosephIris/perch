// The sidebar's Inbox row calls you to act: new emails, pending follow-ups,
// and a sync that broke (so the list is going stale) — each shown and named
// in its tooltip.

import { test } from "node:test";
import assert from "node:assert/strict";
import { inboxSummary } from "../src/inbox-summary.js";

const counts = (n: number, p: number) => ({ new: n, read: 0, pending: p, done: 0 });

test("quiet when nothing wants you", () => {
  const s = inboxSummary({ status: "ok", counts: counts(0, 0) });
  assert.deepEqual([s.fresh, s.pending, s.problem, s.title], [0, 0, null, "Open inbox"]);
});

test("counts new and pending", () => {
  const s = inboxSummary({ status: "ok", counts: counts(3, 2) });
  assert.equal(s.fresh, 3);
  assert.equal(s.pending, 2);
  assert.equal(s.title, "Open inbox · 3 new · 2 pending");
});

test("a broken sync is flagged, a sign-in in progress is not", () => {
  assert.equal(inboxSummary({ status: "error", counts: counts(0, 0) }).problem, "error");
  assert.equal(inboxSummary({ status: "error", needsLogin: true, counts: counts(1, 0) }).problem, "login");
  assert.equal(inboxSummary({ status: "error", needsLogin: true, loggingIn: true, counts: counts(0, 0) }).problem, "error");
  assert.equal(inboxSummary({ status: "syncing", needsLogin: true, loggingIn: true, counts: counts(0, 0) }).problem, null);
  assert.match(inboxSummary({ status: "error", counts: counts(0, 1) }).title, /last sync failed · 1 pending/);
});
