// The sidebar's search: every word must appear in one of the tab's texts,
// ignoring case, the way the Inbox's search works.

import { test } from "node:test";
import assert from "node:assert/strict";
import { sessionMatches } from "../src/sidebar.js";

const tab = ["Fix CDN cache", "perch", "feat/cdn-cache", "", "Look at why creatives are stale"];

test("every word, anywhere, any case", () => {
  assert.equal(sessionMatches(tab, "cdn"), true);
  assert.equal(sessionMatches(tab, "PERCH stale"), true);          // project + pane name
  assert.equal(sessionMatches(tab, "cdn  binance"), false);        // one word missing
});

test("a blank search matches everything", () => {
  assert.equal(sessionMatches(tab, "   "), true);
});
