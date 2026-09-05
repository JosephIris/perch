// Permission-dialog detection for the state-reconciliation probe.
//
// The stakes are asymmetric and the tests encode that: a dialog we FAIL to
// see demotes a genuinely blocked agent (the one unforgivable error), while a
// marker falsely present merely delays the heal. So the dialog samples here
// must ALL read visible, in every selection position and phrasing — and the
// non-dialog tails (working spinner, idle REPL) must read clear, or the probe
// never heals the stuck-permission bug it exists for.

import { test } from "node:test";
import assert from "node:assert/strict";
import { permissionDialogVisible, blockedDialogVisible } from "../src/perm-probe.js";

const PAD =
  "● I'll update the config and rerun the build to confirm.\n" +
  "  Reading src/app/config.ts\n  Writing src/app/config.ts\n";

test("bash permission dialog is visible, wherever the selector sits", () => {
  const dialog = (sel: number) =>
    PAD +
    "╭─────────────────────────────────────────────╮\n" +
    "│ Bash command                                │\n" +
    "│   git push origin main                      │\n" +
    "│ Do you want to proceed?                     │\n" +
    `${sel === 1 ? "│ ❯ 1. Yes" : "│   1. Yes"}\n` +
    `${sel === 2 ? "│ ❯ 2. Yes, and don't ask again this session" : "│   2. Yes, and don't ask again this session"}\n` +
    `${sel === 3 ? "│ ❯ 3. No, and tell Claude what to do differently (esc)" : "│   3. No, and tell Claude what to do differently (esc)"}\n` +
    "╰─────────────────────────────────────────────╯\n";
  assert.equal(permissionDialogVisible(dialog(1)), true);
  assert.equal(permissionDialogVisible(dialog(2)), true);
  assert.equal(permissionDialogVisible(dialog(3)), true);
});

test("codex's approval card is visible, in each of its phrasings", () => {
  const run =
    PAD +
    "Would you like to run the following command?\n" +
    "  python -u temp/pull_events.py\n" +
    "> 1. Yes, proceed\n" +
    "  2. Yes, and don't ask again for commands that start with python\n" +
    "  3. No, and tell Codex what to do differently\n";
  assert.equal(permissionDialogVisible(run), true);
  const grant =
    PAD +
    "Would you like to grant these permissions?\n" +
    "  Network access to api.example.com\n" +
    "  1. Yes, grant these permissions for this turn\n" +
    "  2. No, continue without permissions\n";
  assert.equal(permissionDialogVisible(grant), true);
});

test("edit-file permission dialog is visible", () => {
  const tail =
    PAD +
    "│ Do you want to make this edit to config.ts? │\n" +
    "│ ❯ 1. Yes                                    │\n";
  assert.equal(permissionDialogVisible(tail), true);
});

test("working spinner tail is NOT a dialog — esc-to-interrupt must not pin the state", () => {
  const tail =
    PAD +
    "✻ Compacting the diff… (esc to interrupt · 32s · ↓ 1.2k tokens)\n";
  assert.equal(permissionDialogVisible(tail), false);
});

test("idle REPL tail after an Esc'd prompt is NOT a dialog", () => {
  const tail =
    PAD +
    "> \n" +
    "  ? for shortcuts                        context left: 62%\n";
  assert.equal(permissionDialogVisible(tail), false);
});

test("a nearly-empty tail counts as visible — absence can't be proven yet", () => {
  // Fresh page reload: the buffer hasn't been repainted. Demoting here could
  // silence a genuinely blocked agent, so the empty tail stays 'visible'.
  assert.equal(permissionDialogVisible(""), true);
  assert.equal(permissionDialogVisible("\n\n   \n"), true);
});

test("Claude's own 'do you want to' prose delays the heal (accepted trade)", () => {
  // Recall over precision: the phrasing in a REPLY keeps the state a while
  // longer; the alternative (tighter regex) risks missing a real dialog.
  const tail = PAD + "Do you want to proceed with plan B instead?\n> \n";
  assert.equal(permissionDialogVisible(tail), true);
});

// ---- Inverse detection (blockedDialogVisible) --------------------------------
// This one PROMOTES a calm pane to "Needs you", so the bias flips: promotion
// needs positive evidence, and prose must never provide it.

test("inverse: boxed question/plan dialogs are positive evidence", () => {
  const plan =
    PAD +
    "│ Would you like to proceed with this plan? │\n" +
    "│ ❯ 1. Yes, and auto-accept edits           │\n";
  assert.equal(blockedDialogVisible(plan), true);

  const question =
    PAD +
    "│ Which approach should I take?  │\n" +
    "│ ❯ 1. Feature-flag rollout      │\n" +
    "│   2. Hard cutover              │\n";
  assert.equal(blockedDialogVisible(question), true);
});

test("inverse: Claude's UNBOXED prose question is NOT evidence", () => {
  // "Do you want me to…?" is how half of Claude's replies end, sitting at
  // exactly the buffer bottom of a genuinely-done pane. Promoting on it would
  // rebuild the false-'Needs you' bug this machinery exists to kill.
  const tail = PAD + "Do you want me to apply the same fix to the other DAGs?\n> \n";
  assert.equal(blockedDialogVisible(tail), false);
});

test("inverse: idle REPL and working-spinner tails are NOT evidence", () => {
  assert.equal(blockedDialogVisible(PAD + "> \n  ? for shortcuts\n"), false);
  assert.equal(
    blockedDialogVisible(PAD + "✻ Reticulating splines… (esc to interrupt)\n"),
    false
  );
});

test("inverse: an empty tail is NOT evidence — the mirror of the forward bias", () => {
  assert.equal(blockedDialogVisible(""), false);
  assert.equal(blockedDialogVisible("\n  \n"), false);
});

test("inverse: a bare ❯ menu counts (host gates it to inactive + inferred-done)", () => {
  // /model-style pickers match on purpose: an ABANDONED menu in a background
  // pane genuinely awaits input. The user-driving-it case is excluded by the
  // page (active pane) and host (hook-asserted done), not the regex.
  const tail = PAD + "Select model:\n❯ 1. Sonnet\n  2. Opus\n";
  assert.equal(blockedDialogVisible(tail), true);
});
