// A project chat's board: which column a card sits in, and the lines its
// buttons send the chat in your name. The column order is the product: a
// decision wins over everything; a question only you can settle comes next;
// then whether the card has a result yet.

import { test } from "node:test";
import assert from "node:assert/strict";
import { columnOf, lines, doneLabel, tallyLine } from "../src/review-board.js";
import type { BoardItemView } from "../src/bridge.js";

const item = (over: Partial<BoardItemView>): BoardItemView => ({
  key: "PK-1", title: "T", fullTitle: "", thread: 0, tone: "checking", label: "Checking", status: "", finding: "",
  question: "", draftPath: "", draft: "", url: "", done: "", note: "", updatedMs: 0, ...over,
});

test("columns: done, then needs you, then checking, then ready", () => {
  assert.equal(columnOf(item({ done: "posted", question: "still?", tone: "pending" })), "done");
  assert.equal(columnOf(item({ tone: "ok", question: "Deployed yet?" })), "you");
  assert.equal(columnOf(item({ tone: "pending" })), "you");
  assert.equal(columnOf(item({ tone: "checking" })), "checking");
  assert.equal(columnOf(item({ tone: "bad" })), "ready");
  assert.equal(columnOf(item({ tone: "manual", question: "  " })), "ready");
});

test("buttons send plain lines the chat understands", () => {
  assert.equal(lines.approve("PK-7335"), "approve PK-7335");
  assert.equal(lines.skip("PK-7335"), "skip PK-7335");
  assert.equal(lines.answer("PK-7321", " drop that line \n"), "PK-7321: drop that line");
  assert.equal(lines.approveWith("PK-1", "\n**Verified**\n"), "approve PK-1 with this comment:\n\n**Verified**");
});

test("a decided card says what was done", () => {
  assert.equal(doneLabel(item({ done: "posted", note: "moved to Verified" })), "Posted · moved to Verified");
  assert.equal(doneLabel(item({ done: "skipped" })), "Skipped");
});

test("the tally counts by verdict, in a fixed order", () => {
  const items = [
    item({ tone: "manual", label: "Manual check" }), item({ tone: "ok", label: "Verified", done: "posted" }),
    item({ tone: "manual", label: "Manual check" }), item({ tone: "bad", label: "Not working" }),
  ];
  assert.equal(tallyLine(items), "4 items · 1 verified · 1 not working · 2 manual check · 1 done");
});
