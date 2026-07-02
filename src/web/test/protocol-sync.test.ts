// Protocol drift tripwire. The page → host wire contract lives in THREE
// places that must agree:
//
//   1. bridge.ts  — the OutMessage union (this side)
//   2. src/Perch/PageMessages.cs — the C# DTOs
//   3. MainWindow.BuildRouter    — the dispatch registrations
//
// This test parses the OutMessage union straight out of bridge.ts source and
// compares it against the list below, which mirrors what the C# router
// registers (pinned by Perch.Tests/ProtocolTests.cs on the other side).
// Adding/renaming/removing a message in bridge.ts fails HERE with a reminder
// to update the C# side — turning silent cross-language drift into a red CI
// check.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// Keep sorted. Must match MainWindow.BuildRouter's registrations 1:1.
const EXPECTED_TYPES = [
  "commits.request",
  "onboarding.seen",
  "pane.ack",
  "pane.chooser.choose",
  "pane.close",
  "pane.cwd",
  "pane.focus",
  "pane.in",
  "pane.move",
  "pane.moveDir",
  "pane.recolor",
  "pane.rename",
  "pane.resize",
  "pane.resizeSplit",
  "pane.split",
  "prefs.set",
  "ready",
  "render.pong",
  "resume.decision",
  "session.close",
  "session.new",
  "session.purge",
  "session.rename",
  "session.restore",
  "session.select",
  "settings.request",
  "settings.save",
  "update.apply",
  "update.check",
  "url.open",
  "urlpane.dispose",
  "urlpane.layout",
];

function outMessageTypesFromSource(): string[] {
  // npm test runs with cwd = src/web (run-tests.mjs bundles into .test-out
  // but node's cwd stays at the package root).
  const src = readFileSync(join(process.cwd(), "src", "bridge.ts"), "utf8");
  const start = src.indexOf("export type OutMessage");
  assert.ok(start >= 0, "bridge.ts no longer declares `export type OutMessage`");
  const end = src.indexOf("export type", start + 1);
  const union = src.slice(start, end === -1 ? undefined : end);
  const types = [...union.matchAll(/\{\s*type:\s*"([^"]+)"/g)].map((m) => m[1]);
  return [...new Set(types)].sort();
}

test("bridge.ts OutMessage union matches the host router's registrations", () => {
  const actual = outMessageTypesFromSource();
  assert.deepEqual(
    actual,
    EXPECTED_TYPES,
    "\nOutMessage drifted from the host protocol.\n" +
      "If you added/renamed a page → host message, update ALL of:\n" +
      "  1. src/Perch/PageMessages.cs   (the DTO record)\n" +
      "  2. MainWindow.BuildRouter      (the dispatch registration)\n" +
      "  3. Perch.Tests/ProtocolTests.cs (a round-trip test)\n" +
      "  4. EXPECTED_TYPES in this file\n"
  );
});
