// The words and numbers a project chat draws its threads with: which "#n" in
// Claude's prose is a thread, the one line a run of tools folds into, what a
// thread's row says it is doing, how old it is, and the model's name.

import { test } from "node:test";
import assert from "node:assert/strict";
import { splitThreadRefs, summarizeWork, modelLabel, shortAge, statusLine, groupOf, dayLabel } from "../src/thread-ui.js";
import type { SessionView } from "../src/bridge.js";

const t = (over: Partial<SessionView>): SessionView => ({
  id: "x", title: "T", agentState: "idle", threadNumber: 1, activityDetail: "", notification: null, ...over,
} as SessionView);

test("#n becomes a thread only when it is one", () => {
  const known = (n: number) => n === 3 || n === 12;
  assert.deepEqual(splitThreadRefs("#3 found it, and #12 too.", known), [3, " found it, and ", 12, " too."]);
  assert.deepEqual(splitThreadRefs("see #7", known), ["see #7"]);
  // Not inside a word, a URL fragment, or an issue/PR number.
  assert.deepEqual(splitThreadRefs("page#3 and a/#3", known), ["page#3 and a/#3"]);
  assert.deepEqual(splitThreadRefs("PR #3 is up; issue #12 too", known), ["PR #3 is up; issue #12 too"]);
  assert.deepEqual(splitThreadRefs("(#3)", known), ["(", 3, ")"]);
  assert.deepEqual(splitThreadRefs("", known), []);
});

test("a run of tools folds into one line", () => {
  assert.equal(summarizeWork([
    { verb: "Read", target: "a.cs" }, { verb: "Read", target: "a.cs" }, { verb: "Read", target: "b.cs" },
    { verb: "Bash", target: "git log" }, { verb: "Grep", target: "Foo" },
  ]), "Read 2 files, ran 1 command, searched 1 time");
  assert.equal(summarizeWork([{ verb: "Edit", target: "x" }, { verb: "PowerShell", target: "dotnet test" }, { verb: "PowerShell", target: "git status" }]),
    "Edited 1 file, ran 2 commands");
  // Task-list updates show as the checklist, not here.
  assert.equal(summarizeWork([{ verb: "TaskUpdate", target: "" }]), "");
});

test("model ids read the way people say them", () => {
  assert.equal(modelLabel("claude-opus-5-5[1m]"), "Opus 5.5");
  assert.equal(modelLabel("claude-sonnet-5"), "Sonnet 5");
  assert.equal(modelLabel("claude-haiku-4-5-20251001"), "Haiku 4.5");
  assert.equal(modelLabel("gpt-something-else-x"), "gpt-something-else-x");
});

test("ages: now, then one unit", () => {
  assert.equal(shortAge(10_000), "now");
  assert.equal(shortAge(3 * 60_000), "3m");
  assert.equal(shortAge(2 * 3_600_000 + 5 * 60_000), "2h");
  assert.equal(shortAge(4 * 86_400_000), "4d");
});

test("the status line says what it is doing, or what it needs", () => {
  assert.deepEqual(statusLine(t({ agentState: "permission", notification: { text: "Run npm install?", level: "info" } })),
    { text: "Run npm install?", blocked: true });
  assert.deepEqual(statusLine(t({ agentState: "working", threadTaskNow: "Running the tests", activityDetail: "Bash" })),
    { text: "Running the tests", blocked: false });
  assert.deepEqual(statusLine(t({ agentState: "working", activityDetail: "Editing Foo.cs" })), { text: "Editing Foo.cs", blocked: false });
  assert.deepEqual(statusLine(t({ agentState: "done", threadUnmerged: 2, threadReply: "**Fixed** the bug.\nMore." })),
    { text: "Fixed the bug.", blocked: false });
  assert.equal(groupOf(t({ agentState: "done", threadUnmerged: 2 })), "ready");
  assert.deepEqual(statusLine(t({ agentState: "idle" })), { text: "No report yet", blocked: false });
  assert.deepEqual(statusLine(t({ agentState: "working", dormant: true, threadReply: "Done." })), { text: "Done.", blocked: false });
});

test("day rules: today, yesterday, then the date", () => {
  const now = new Date(2026, 8, 24, 15, 0).getTime();
  assert.equal(dayLabel(new Date(2026, 8, 24, 0, 5).getTime(), now), "Today");
  assert.equal(dayLabel(new Date(2026, 8, 23, 23, 59).getTime(), now), "Yesterday");
  assert.match(dayLabel(new Date(2026, 8, 20).getTime(), now), /20/);
  assert.match(dayLabel(new Date(2025, 8, 20).getTime(), now), /2025/);
});
