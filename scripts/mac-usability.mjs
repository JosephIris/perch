#!/usr/bin/env node
// macOS usability suite: drives the running app end-to-end through the
// control pipe (PERCH_ENABLE_TEST_IPC) — every step is a real user-visible
// feature, asserted against `state.dump` and captured as a screenshot so a
// human can review what the feature actually looked like.
//
// Usage:
//   PERCH_DATA_DIR=<dir> node scripts/mac-usability.mjs [--out <shots-dir>]
//
// The suite owns the app lifecycle: it kills any running instance, launches
// a fresh one from src/Perch.Mac/bin/Debug/net8.0, runs the feature tests,
// then SIGTERMs and relaunches to prove persistence. Exit code = number of
// failed tests.

import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { execFileSync, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const appDir = path.join(repo, "src/Perch.Mac/bin/Debug/net8.0");
const dataDir = process.env.PERCH_DATA_DIR
  ?? path.join(os.tmpdir(), "perch-usability-data");
const logPath = path.join(dataDir, "perch", "errors.log");
const outIdx = process.argv.indexOf("--out");
const outDir = outIdx > 0 ? process.argv[outIdx + 1] : path.join(os.tmpdir(), "perch-usability");
fs.mkdirSync(outDir, { recursive: true });

const sockPath = path.join(os.tmpdir(), "CoreFxPipe_perch\\control");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let appProc = null;
let cachedWinId = null;

function launchApp() {
  appProc = spawn(path.join(appDir, "Perch"), [], {
    cwd: appDir,
    env: {
      ...process.env,
      DOTNET_ROOT: path.join(os.homedir(), ".dotnet"),
      PERCH_DATA_DIR: dataDir,
      PERCH_ENABLE_TEST_IPC: "1",
    },
    detached: false,
    stdio: "ignore",
  });
  cachedWinId = null;
}

function killApp(signal = "SIGTERM") {
  try { execFileSync("pkill", ["-x", "Perch"]); } catch { /* none running */ }
}

/** Write one JSON line to the control pipe. */
function send(obj) {
  return new Promise((resolve, reject) => {
    const c = net.connect(sockPath);
    c.on("connect", () => c.end(JSON.stringify(obj) + "\n", resolve));
    c.on("error", reject);
  });
}

/** state.dump → parsed STATE_DUMP json (the log is the reply channel). */
async function dump() {
  await send({ verb: "state.dump" });
  await sleep(400);
  const lines = fs.readFileSync(logPath, "utf8").split("\n");
  const last = lines.reverse().find((l) => l.includes("STATE_DUMP"));
  if (!last) throw new Error("no STATE_DUMP in log");
  return JSON.parse(last.slice(last.indexOf("STATE_DUMP") + "STATE_DUMP".length));
}

function activeSession(d) {
  return d.sessions.find((s) => s.active) ?? d.sessions[0];
}

/** Last logged pty.snapshot byte count (send the verb first). */
async function ptyBytes() {
  await send({ verb: "pty.snapshot" });
  await sleep(300);
  const lines = fs.readFileSync(logPath, "utf8").split("\n");
  const last = lines.reverse().find((l) => l.includes("Pty.snapshot"));
  const m = last?.match(/bytes=(\d+)/);
  return m ? parseInt(m[1], 10) : -1;
}

function windowId() {
  if (cachedWinId) return cachedWinId;
  const out = execFileSync("swift", [path.join(repo, "scripts/mac-window-id.swift")], {
    encoding: "utf8",
  }).trim();
  cachedWinId = out.split("\n")[0];
  return cachedWinId;
}

function shot(name) {
  try {
    execFileSync("screencapture", ["-x", "-o", `-l${windowId()}`, path.join(outDir, `${name}.png`)]);
  } catch (e) {
    console.log(`  (screenshot ${name} failed: ${e.message})`);
  }
}

function countErrors() {
  try {
    return fs.readFileSync(logPath, "utf8").split("\n").filter((l) => l.includes("ERROR")).length;
  } catch { return 0; }
}

const results = [];
async function test(name, fn) {
  try {
    await fn();
    results.push({ name, ok: true });
    console.log(`PASS ${name}`);
  } catch (e) {
    results.push({ name, ok: false, err: String(e.message ?? e) });
    console.log(`FAIL ${name}: ${e.message ?? e}`);
    shot(`FAIL-${name}`);
  }
}
function assert(cond, msg) { if (!cond) throw new Error(msg); }

// ---------------------------------------------------------------------------

killApp();
await sleep(1000);
fs.rmSync(logPath, { force: true });
// Skip the first-run onboarding dialog — it would sit over every screenshot.
// (Merged into existing settings if the data dir has been used before.)
const settingsPath = path.join(dataDir, "perch", "settings.json");
fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
let settings = {};
try { settings = JSON.parse(fs.readFileSync(settingsPath, "utf8")); } catch { /* fresh dir */ }
settings.OnboardingSeen = true;
fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2));
launchApp();
console.log("launching app…");
await sleep(8000);

const errorsAtBoot = countErrors();

await test("01-boot", async () => {
  const d = await dump();
  assert(d.sessions.length >= 1, "no sessions after boot");
  assert(errorsAtBoot === 0, `${errorsAtBoot} ERRORs during boot`);
  shot("01-boot");
});

await test("02-terminal-echo", async () => {
  const before = await ptyBytes();
  assert(before >= 0, "no pty snapshot");
  await send({ verb: "pty.send", text: "echo USABILITY-$((41+1))\r" });
  await sleep(1200);
  const after = await ptyBytes();
  assert(after > before + 10, `pty bytes did not grow (${before} → ${after})`);
  shot("02-terminal-echo");
});

let newSessionId = null;
await test("03-session-new", async () => {
  const before = (await dump()).sessions.length;
  await send({ verb: "session.new" });
  await sleep(800);
  const d = await dump();
  assert(d.sessions.length === before + 1, `session count ${d.sessions.length} != ${before + 1}`);
  newSessionId = activeSession(d).id;
  shot("03-session-new");
});

await test("04-session-rename", async () => {
  await send({ verb: "session.rename", id: newSessionId, title: "usability run" });
  await sleep(500);
  const d = await dump();
  // state.dump doesn't carry the tab title, so assert indirectly: no error
  // logged and the session still exists; the screenshot shows the name.
  assert(d.sessions.some((s) => s.id === newSessionId), "renamed session vanished");
  shot("04-session-rename");
});

await test("05-split-right", async () => {
  // Spawn the new session's pane first (lazy): a resize-shaped nudge comes
  // from the page, but the harness can just split — the split targets the
  // active pane id regardless of PTY state.
  await send({ verb: "pane.split-active", dir: "right" });
  await sleep(800);
  const d = await dump();
  assert(activeSession(d).panes.length === 2, `pane count ${activeSession(d).panes.length} != 2`);
  shot("05-split-right");
});

await test("06-split-down", async () => {
  await send({ verb: "pane.split-active", dir: "down" });
  await sleep(800);
  const d = await dump();
  assert(activeSession(d).panes.length === 3, `pane count != 3`);
  shot("06-split-down");
});

await test("07-pane-close", async () => {
  await send({ verb: "pane.close-active" });
  await sleep(800);
  const d = await dump();
  assert(activeSession(d).panes.length === 2, `pane count != 2 after close`);
  shot("07-pane-close");
});

await test("08-prefs-fontsize", async () => {
  await send({ verb: "prefs.set", fontSize: 15 });
  await sleep(500);
  const d = await dump();
  assert(d.prefs.fontSize === 15, `fontSize ${d.prefs.fontSize} != 15`);
  await send({ verb: "prefs.set", fontSize: 13 });
});

await test("09-board", async () => {
  const before = activeSession(await dump()).panes.map((p) => p.id);
  await send({ verb: "board.new-active" });
  await sleep(800);
  const d = await dump();
  const panes = activeSession(d).panes.map((p) => p.id);
  assert(panes.length === before.length + 1, "board pane did not appear");
  const boardId = panes.find((id) => !before.includes(id));
  await send({
    verb: "board.add", paneId: boardId, kind: "note",
    // Near-origin so the card is inside the viewport even when the board
    // pane is a narrow column.
    text: "usability note — written by the harness", x: 16, y: 16, origin: "user",
  });
  await sleep(800);
  shot("09-board");
});

await test("10-agent-status", async () => {
  // Type a real `perch status` into the FIRST session's shell (pty.send hits
  // the active session's first leaf — switch back to session 1 first).
  const d0 = await dump();
  const first = d0.sessions[0];
  await send({ verb: "session.select", id: first.id });
  await sleep(600);
  await send({ verb: "pty.send", text: "perch status working --detail 'usability suite'\r" });
  await sleep(1500);
  const d = await dump();
  const pane = d.sessions[0].panes[0];
  assert(pane.agentState === "working", `agentState ${pane.agentState} != working`);
  shot("10-agent-status");
});

await test("11-agent-notify", async () => {
  // perch's CLI convention is flags-before-text (see CmdNotify).
  await send({ verb: "pty.send", text: "perch notify --level warn 'usability ping'\r" });
  await sleep(1500);
  const d = await dump();
  const pane = d.sessions[0].panes[0];
  assert((pane.notification ?? "") !== "", "notification not recorded");
  shot("11-agent-notify");
});

await test("12-render-ping", async () => {
  await send({ verb: "render.ping", id: 4242 });
  await sleep(800);
  const log = fs.readFileSync(logPath, "utf8");
  assert(log.includes("RenderPong") || log.includes("render.pong") || log.includes("pong id=4242"),
    "no render pong logged — page main thread didn't answer");
});

await test("13-project-add", async () => {
  await send({ verb: "project.add", path: repo });
  await sleep(800);
  await send({ verb: "ui.mode", mode: "projects" });
  await sleep(400);
  assert(countErrors() === errorsAtBoot, "errors logged during project add");
  shot("13-project-add");
});

let closedId = null;
await test("14-session-close-restore", async () => {
  const d0 = await dump();
  closedId = d0.sessions.find((s) => s.id !== d0.sessions[0].id)?.id ?? d0.sessions[0].id;
  const before = d0.sessions.length;
  await send({ verb: "session.close", id: closedId });
  await sleep(800);
  let d = await dump();
  assert(d.sessions.length === before - 1, "session did not close");
  assert(d.closedSessions.length >= 1, "closed session not in closedSessions");
  await send({ verb: "session.restore", id: closedId });
  await sleep(800);
  d = await dump();
  assert(d.sessions.length === before, "session did not restore");
  shot("14-session-restore");
});

await test("15-settings-dialog", async () => {
  await send({ verb: "ui.open-settings" });
  await sleep(800);
  shot("15-settings-dialog");
  // No close verb — the persistence restart below resets the page anyway.
});

await test("16-persistence-across-restart", async () => {
  const before = await dump();
  const names = before.sessions.map((s) => s.panes.length).join(",");
  killApp();               // SIGTERM — the closing handler runs Shutdown()
  await sleep(1500);
  launchApp();
  await sleep(8000);
  const after = await dump();
  const namesAfter = after.sessions.map((s) => s.panes.length).join(",");
  assert(after.sessions.length === before.sessions.length,
    `session count ${after.sessions.length} != ${before.sessions.length} after restart`);
  assert(namesAfter === names, `pane layout ${namesAfter} != ${names} after restart`);
  shot("16-persistence");
});

await test("17-clean-log", async () => {
  const errs = countErrors();
  assert(errs === 0, `${errs} ERROR lines in the final log`);
});

// ---------------------------------------------------------------------------

const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} passed` +
  (failed.length ? ` — FAILED: ${failed.map((f) => f.name).join(", ")}` : ""));
fs.writeFileSync(path.join(outDir, "results.json"), JSON.stringify(results, null, 2));
console.log(`screenshots + results in ${outDir}`);
process.exit(failed.length);
