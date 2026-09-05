#!/usr/bin/env node
// macOS END-TO-END gate. Real Perch (the mac host), real Claude Code, no
// fakes — the mac twin of scripts/verify-comms.ps1, plus the two things that
// broke on the mac first: an app-driven `claude` start and the codex pane.
//
// Usage:
//   node scripts/mac-e2e.mjs [--quick] [--out <dir>] [--model haiku]
//                            [--app <path/to/Perch>] [--sections a,b,c,d,e1..e5]
//
//   --quick   skip the team-room sections (no tokens spent). Sections a–d
//             still run a real `claude` once (a few cents on haiku is the
//             brief; an interactive session that just boots costs nothing).
//
// What it proves, in order:
//
//   a. boot        the app comes up, a pane spawns, the login-shell PATH was
//                  adopted (MacShellEnv), no ERROR lines.
//   b. typed       `claude` typed into a pane reports its session through the
//                  hooks; the trust prompt is answered by the suite.
//   c. tab         a project tab with agent=claude starts Claude BY ITSELF —
//                  the pane's initial command runs under `sh -c` before any
//                  rc file, so this is the path a Dock launch broke. The app
//                  is launched with launchd's bare PATH for exactly that
//                  reason (--inherit-path to opt out).
//   d. codex       a codex tab runs the bundled shim: the pane reports
//                  agent=codex, and the wrapper wrote perch.config.toml into
//                  CODEX_HOME with hooks pointing at the bundled perch.
//                  A FAKE codex is used (it only has to be launched) so the
//                  section is deterministic and needs no codex install.
//   e1–e5. room    the delivery gate, section for section as on Windows:
//                  warm delivery, cold delivery after a restart, Send again,
//                  a permission card answered at human speed, bot-to-bot.
//
// ISOLATION: its own PERCH_DATA_DIR, its own CODEX_HOME, a throwaway repo,
// and it kills only the PID it launched (the user's Perch.app keeps running
// — MacSingleInstance is per data dir). The real ~/.claude is used on
// purpose: real CLI, real hooks, real trust prompt.
//
// Exit code = number of failed checks. The data dir (with errors.log) is kept
// on failure and removed on success.

import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { execFileSync, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const argv = process.argv.slice(2);
const flag = (name) => { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : null; };
const has = (name) => argv.includes(name);

const appBin = flag("--app") ?? path.join(repo, "src/Perch.Mac/bin/Debug/net8.0/Perch");
const appDir = path.dirname(appBin);
const toolsDir = path.join(appDir, "tools");
const botModel = flag("--model") ?? "haiku";
const quick = has("--quick");
const inheritPath = has("--inherit-path");
const sections = (flag("--sections") ?? (quick ? "a,b,c,d" : "a,b,c,d,e1,e2,e3,e4,e5")).split(",");
const outDir = flag("--out") ?? path.join(os.tmpdir(), "perch-e2e-out");
fs.mkdirSync(outDir, { recursive: true });

const dataDir = path.join(os.tmpdir(), `perch-e2e-${process.pid}`);
const perchDir = path.join(dataDir, "perch");
const logPath = path.join(perchDir, "errors.log");
const repoDir = path.join(dataDir, "e2e-repo");
const codexHome = path.join(dataDir, "codex-home");
const fakeBin = path.join(dataDir, "fakebin");
const sockPath = path.join(os.tmpdir(), "CoreFxPipe_perch\\control");
// launchd's PATH for a Dock launch — what Perch.app really starts with.
const bareLaunchdPath = "/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const ESC = "\x1b";

// ---------------------------------------------------------------------------
// Preconditions

if (!fs.existsSync(appBin)) die(`Perch not built: ${appBin} (dotnet build src/Perch.Mac/Perch.Mac.csproj)`);
if (!fs.existsSync(path.join(toolsDir, "perch"))) die(`bundled perch CLI missing: ${toolsDir}/perch`);
if (fs.existsSync(sockPath)) {
  const live = await new Promise((resolve) => {
    const c = net.connect(sockPath);
    c.on("connect", () => { c.end(); resolve(true); });
    c.on("error", () => resolve(false));
  });
  if (live) die("control pipe already exists — another test-IPC Perch is running.");
  // Left behind by an instance that was killed outright; the kernel dropped
  // the listener with it, so the path is just a stale inode.
  fs.rmSync(sockPath, { force: true });
}
let realClaude = null;
try { realClaude = execFileSync(process.env.SHELL || "/bin/zsh", ["-ilc", "command -v claude"], { encoding: "utf8" }).trim().split("\n").pop(); } catch { /* not found */ }
if (!realClaude) die("claude is not on the login shell's PATH");

function die(msg) { console.error(`ERROR: ${msg}`); process.exit(1); }

// ---------------------------------------------------------------------------
// App lifecycle — one PID, ours.

let app = null;
function launch(extraEnv = {}) {
  const env = { ...process.env, ...extraEnv };
  // Claude Code nesting markers: a harness run from inside a Claude session
  // would otherwise make every pane's claude a child session.
  for (const k of Object.keys(env)) if (k.startsWith("CLAUDE")) delete env[k];
  env.DOTNET_ROOT = env.DOTNET_ROOT || path.join(os.homedir(), ".dotnet");
  env.PERCH_DATA_DIR = dataDir;
  env.PERCH_ENABLE_TEST_IPC = "1";
  env.CODEX_HOME = codexHome;
  env.PERCH_HOOK_DUMP = path.join(perchDir, "hook-dump.jsonl");
  // The fake codex must be reachable from the resolved login PATH too, so it
  // rides in the harness's own PATH (MacShellEnv keeps entries it lacks).
  env.PATH = (inheritPath ? env.PATH : bareLaunchdPath) + ":" + fakeBin;
  app = spawn(appBin, [], { cwd: appDir, env, detached: false, stdio: "ignore" });
  const pid = app.pid;
  app.on("exit", (code) => { if (app && app.pid === pid) { app = null; console.log(`  (Perch pid ${pid} exited, code ${code})`); } });
  return pid;
}
async function stop(force = false) {
  if (!app) return;
  const p = app;
  app = null;
  try { p.kill(force ? "SIGKILL" : "SIGTERM"); } catch { /* gone */ }
  const end = Date.now() + 6000;
  while (Date.now() < end && p.exitCode === null && p.signalCode === null) await sleep(200);
  if (p.exitCode === null && p.signalCode === null) { try { p.kill("SIGKILL"); } catch { /* gone */ } await sleep(500); }
}

// ---------------------------------------------------------------------------
// Control pipe + log helpers

function send(obj) {
  return new Promise((resolve, reject) => {
    const c = net.connect(sockPath);
    c.on("connect", () => c.end(JSON.stringify(obj) + "\n", resolve));
    c.on("error", reject);
  });
}
const readLog = () => { try { return fs.readFileSync(logPath, "utf8"); } catch { return ""; } };
const logLines = () => readLog().split("\n");
const count = (pat) => logLines().filter((l) => l.includes(pat)).length;
const last = (pat) => logLines().reverse().find((l) => l.includes(pat)) ?? "";
async function waitUntil(cond, ms, step = 500) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try { if (await cond()) return true; } catch { /* keep waiting */ }
    await sleep(step);
  }
  return false;
}
async function dump() {
  const before = count("STATE_DUMP");
  await send({ verb: "state.dump" });
  if (!await waitUntil(() => count("STATE_DUMP") > before, 8000, 150)) throw new Error("state.dump never logged");
  const line = last("STATE_DUMP");
  return JSON.parse(line.slice(line.indexOf("STATE_DUMP") + "STATE_DUMP".length));
}
async function teamDump(projectId) {
  await send({ verb: "team.request", projectId });
  await sleep(300);
  const before = count("TEAM_DUMP");
  await send({ verb: "team.dump", projectId });
  if (!await waitUntil(() => count("TEAM_DUMP") > before, 10000, 200)) throw new Error("team.dump never logged");
  const line = last("TEAM_DUMP");
  return JSON.parse(line.slice(line.indexOf("TEAM_DUMP") + "TEAM_DUMP".length));
}
let cachedWinId = null;
function shot(name) {
  try {
    if (!cachedWinId) {
      // By PID: the user's own Perch.app may be open too.
      const out = execFileSync("swift", [path.join(repo, "scripts/mac-window-id.swift"), String(app?.pid ?? "")], { encoding: "utf8" }).trim();
      cachedWinId = out.split("\n")[0];
    }
    execFileSync("screencapture", ["-x", "-o", `-l${cachedWinId}`, path.join(outDir, `${name}.png`)]);
  } catch (e) { console.log(`  (screenshot ${name} skipped: ${String(e.message).split("\n")[0]})`); }
}
const errorLines = () => logLines().filter((l) => l.includes(" ERROR "));
// projects.json is written with PascalCase members (Projects / Id / Path).
function projectByPath(file) {
  const doc = JSON.parse(fs.readFileSync(file, "utf8"));
  const list = doc.Projects ?? doc.projects ?? [];
  const p = list.find((x) => String(x.Path ?? x.path).includes("e2e-repo"));
  if (!p) throw new Error("e2e-repo not in projects.json");
  return { id: p.Id ?? p.id, path: p.Path ?? p.path };
}
/** Terminal output as words: escape sequences (CSI, OSC, the odd ESC-x)
 *  and whitespace removed, because a TUI positions every word with a cursor
 *  move rather than a space. Match against this, never against the raw tail. */
const plain = (t) => t
  .replace(/\x1b\][^\x07\x1b]*(\x07|\x1b\\)/g, "")
  .replace(/\x1b\[[0-9;?<>=]*[ -\/]*[@-~]/g, "")
  .replace(/\x1b./g, "")
  .replace(/\s+/g, "");
/** The pane's recent raw output, decoded (pty.tail → PTY_TAIL log line). */
async function tail(paneId) {
  const tag = `PTY_TAIL pane=${paneN(paneId)} `;
  const before = count(tag);
  await send({ verb: "pty.tail", paneId });
  if (!await waitUntil(() => count(tag) > before, 5000, 100)) return "";
  const line = last(tag);
  const b64 = line.slice(line.indexOf(tag) + tag.length).trim();
  return Buffer.from(b64, "base64").toString("utf8");
}

// ---------------------------------------------------------------------------
// Claude's pre-session prompts. A brand-new folder makes Claude Code ask
// "Do you trust the files in this folder?" before the session starts. For a
// team bot the room raises a card (Team.trust.ask) and the answer is a verb;
// for a plain pane the host only logs that Claude is sitting on an
// interactive prompt (Setup … "quiet before session-start"), and the suite
// answers the way the room does: Down + Enter selects "Yes, I trust".

const paneN = (id) => id.replace(/-/g, "");
async function waitForSession(paneId, ms, before = 0) {
  const tag = `pane=${paneN(paneId)} type=session`;
  const stuck = `pane ${paneN(paneId)} quiet before session-start`;
  const end = Date.now() + ms;
  let answeredStuck = 0;
  let lastAnswer = 0;
  while (Date.now() < end) {
    if (count(tag) > before) return true;
    // Two ways to know Claude is waiting on its trust prompt: the host's
    // pre-session watchdog (only armed for app-started panes), or the words
    // on the pane's screen (any pane).
    let ask = false;
    if (count(stuck) > answeredStuck) { answeredStuck = count(stuck); ask = true; }
    else if (Date.now() - lastAnswer > 6000) {
      const t = plain(await tail(paneId));
      if (/safetycheck|trustthisfolder|Doyoutrust/i.test(t)) ask = true;
      else if (/real`claude`binarynotfound/.test(t)) { console.log("      the shim could not find the real claude on PATH"); return false; }
    }
    if (ask) {
      lastAnswer = Date.now();
      console.log("      Claude is on its trust prompt — answering it");
      await send({ verb: "pty.send", paneId, text: `${ESC}[B\r` });
    }
    await sleep(500);
  }
  return false;
}

// ---------------------------------------------------------------------------
// Checks

const fails = [];
let checks = 0;
function check(name, ok, detail = "") {
  checks++;
  if (ok) console.log(`  [+] ${name}`);
  else { console.log(`  [-] ${name} ${detail}`); fails.push(name); shot(`FAIL-${checks}`); }
  return ok;
}
function section(id, title) {
  const on = sections.includes(id);
  console.log(`\n[${id}] ${title}${on ? "" : "  (skipped)"}`);
  return on;
}

// ---------------------------------------------------------------------------
// Fixtures: data dir, throwaway repo, fake codex, seeded team.

fs.rmSync(dataDir, { recursive: true, force: true });
fs.mkdirSync(perchDir, { recursive: true });
fs.mkdirSync(path.join(repoDir, "src"), { recursive: true });
fs.mkdirSync(codexHome, { recursive: true });
fs.mkdirSync(fakeBin, { recursive: true });
// No first-run dialog over the screenshots; no "resume?" prompt gating spawns.
fs.writeFileSync(path.join(perchDir, "settings.json"), JSON.stringify({ OnboardingSeen: true, ResumeAgentsOnLaunch: true }, null, 2));
fs.writeFileSync(path.join(repoDir, "README.md"), "# e2e-repo\n\nA throwaway repository for the mac end-to-end gate.\n");
fs.writeFileSync(path.join(repoDir, "src/app.ts"), "export const hello = () => 'hi';\n");
execFileSync("git", ["init", "--quiet"], { cwd: repoDir });
execFileSync("git", ["add", "-A"], { cwd: repoDir });
execFileSync("git", ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "init"], { cwd: repoDir });

// The fake codex: prints what it was launched with, then waits to be killed
// with the pane. Enough for the shim to resolve it, bracket it with agent
// IPC and write the hooks profile.
fs.writeFileSync(path.join(fakeBin, "codex"),
  "#!/bin/sh\n" +
  "printf 'FAKE CODEX argv:'; for a in \"$@\"; do printf ' [%s]' \"$a\"; done; printf '\\n'\n" +
  "echo \"CODEX_HOME=$CODEX_HOME\"\n" +
  "exec sleep 600\n");
fs.chmodSync(path.join(fakeBin, "codex"), 0o755);

const teamDir = path.join(repoDir, ".perch/team");
fs.mkdirSync(path.join(teamDir, "positions/courier"), { recursive: true });
fs.writeFileSync(path.join(teamDir, "team.json"), JSON.stringify({
  v: 1,
  positions: [{ slug: "courier", name: "Courier", purpose: "Answers room posts in one line.", referenceRepo: "", model: botModel, createdAtMs: 1, briefGeneratedAtMs: 0, briefModel: "" }],
  bots: [],
}));
fs.writeFileSync(path.join(teamDir, "positions/courier/brief.md"),
`## Role

You answer posts from the team room in ONE short line. Two exceptions, and
only these: use the SendMessage tool when a post tells you to message a
teammate, and run \`perch team post "<line>"\` when a post tells you to post
something to the room. When a TEAMMATE messages you, post what they said to
the room with \`perch team post\` straight away, quoting their word exactly.
Never read a file, never write code, never run anything else.
`);

const answeredCards = new Set();
async function answerCards(projectId) {
  for (const l of logLines()) {
    if (!l.includes("Team.perm.ask")) continue;
    const m = /id=([0-9a-f]+)/.exec(l);
    if (!m || answeredCards.has(m[1])) continue;
    answeredCards.add(m[1]);
    await send({ verb: "team.perm.answer", projectId, id: m[1], decision: "allow" });
  }
}
const ledgerHas = (d, pred) => (d.ledger ?? []).some(pred);
const saysWord = (word) => (e) => (e.kind === "beat" || e.kind === "note") && typeof e.text === "string" && e.text.includes(word);

// `slug` is the BOT slug (the trust card names it); every bot sits in the
// seeded "courier" position.
async function bringUpBot(projectId, nickname, slug) {
  await send({ verb: "team.bot.create", projectId, nickname, positionSlug: "courier", worktree: false });
  const sessionsBefore = count("type=session");
  // Which pane is the bot's? The create line names its session.
  const okFast = await waitUntil(() => count("type=session") > sessionsBefore, 20000);
  if (okFast) return true;
  if (await waitUntil(() => count("Team.trust.ask") >= 1 && last("Team.trust.ask").includes(`bot=${slug}`), 30000)) {
    console.log(`      ${nickname} asks to trust its folder — answering from the room`);
    await send({ verb: "team.bot.answer", projectId, botId: slug, answer: "trust" });
  }
  return await waitUntil(() => count("type=session") > sessionsBefore, 90000);
}

async function relaunchAndWait(extraEnv) {
  await stop();
  await sleep(2000);
  launch(extraEnv);
  cachedWinId = null;
  if (!await waitUntil(() => count("ControlIpc.start") > 0 && fs.existsSync(sockPath), 30000)) throw new Error("control pipe never came up after relaunch");
  await sleep(2500);
}

// ---------------------------------------------------------------------------

let exitCode = 0;
let projectId = null;
try {
  console.log(`mac e2e — app ${appBin}\n         data ${dataDir}\n         PATH ${inheritPath ? "(inherited)" : "launchd's bare PATH"}  model ${botModel}`);
  const t0 = Date.now();
  launch();
  const booted = await waitUntil(() => count("ControlIpc.start") > 0 && fs.existsSync(sockPath), 30000);
  if (!booted) throw new Error("the control pipe never came up (is PERCH_ENABLE_TEST_IPC honoured?)");
  const spawned = await waitUntil(() => count("Pane.spawn") >= 1, 30000);
  await sleep(1500);

  // --- a. boot ------------------------------------------------------------
  if (section("a", "boot: pane, adopted PATH, clean log")) {
    check("the first pane spawned", spawned);
    const d = await dump();
    check("a session exists", d.sessions.length >= 1);
    const shellEnv = last("ShellEnv");
    check("the login shell's PATH was adopted", shellEnv.includes("PATH="), `- ${shellEnv || "no ShellEnv line"}`);
    check("that PATH reaches the real claude", shellEnv.includes(path.dirname(realClaude)), `- claude lives in ${path.dirname(realClaude)}`);
    check("no ERROR during boot", errorLines().length === 0, `- ${errorLines()[0] ?? ""}`);
    shot("a-boot");
  }

  // --- b. claude typed into a pane -----------------------------------------
  let typedPane = null;
  if (section("b", "typed: `claude` in a pane reports its session")) {
    const d = await dump();
    const sess = d.sessions.find((s) => s.active) ?? d.sessions[0];
    typedPane = sess.panes[0].id;
    await send({ verb: "pty.send", paneId: typedPane, text: `cd '${repoDir}'\rclaude\r` });
    const up = await waitForSession(typedPane, 90000);
    check("the session-start hook reached the host", up);
    if (up) {
      const ok = await waitUntil(async () => (await dump()).sessions.flatMap((s) => s.panes).find((p) => p.id === typedPane)?.agentType === "claude", 10000);
      check("the pane wears the Claude badge (agentType=claude)", ok);
      const pane = (await dump()).sessions.flatMap((s) => s.panes).find((p) => p.id === typedPane);
      check("the pane knows its Claude session id", !!pane?.claudeSessionId, `- ${JSON.stringify(pane)}`);
      const state = (await dump()).sessions.flatMap((s) => s.panes).find((p) => p.id === typedPane)?.agentState;
      check("and the host tracks its agent state", ["working", "done", "idle"].includes(state), `- state ${state}`);
      shot("b-typed-claude");
      await send({ verb: "pty.send", paneId: typedPane, text: "/exit\r" });
      await waitUntil(async () => (await dump()).sessions.flatMap((s) => s.panes).find((p) => p.id === typedPane)?.agentType === "", 20000);
    }
  }

  // --- c. a project tab starts claude by itself ----------------------------
  if (section("c", "tab: a project tab with agent=claude starts Claude by itself (Dock PATH)")) {
    await send({ verb: "project.add", path: repoDir });
    const projectsJson = path.join(perchDir, "projects.json");
    const added = await waitUntil(() => fs.existsSync(projectsJson) && fs.readFileSync(projectsJson, "utf8").includes("e2e-repo"), 10000);
    check("the repo was registered as a project", added);
    if (added) {
      const proj = projectByPath(projectsJson);
      projectId = String(proj.id);
      const before = await dump();
      const spawnsBefore = count("Pane.spawn");
      await send({ verb: "project.tab.new", projectId, name: "e2e tab", agent: "claude", worktree: false });
      const tabSpawned = await waitUntil(() => count("Pane.spawn") > spawnsBefore, 30000);
      check("the tab's pane spawned", tabSpawned);
      const after = await dump();
      const fresh = after.sessions.find((s) => !before.sessions.some((b) => b.id === s.id));
      check("a new session appeared for the tab", !!fresh);
      if (fresh) {
        const paneId = fresh.panes[0].id;
        const spawnLine = last(`Pane.spawn: pane=${paneN(paneId)}`);
        check("its initial command is a claude launch", /claude/.test(spawnLine), `- ${spawnLine.slice(0, 160)}`);
        const up = await waitForSession(paneId, 120000);
        check("Claude started and reported its session (the Dock-PATH bug)", up,
          `- no session hook; last shim complaint: ${last("wrap-claude") || "none logged"}`);
        if (up) {
          const named = await waitUntil(async () => (await dump()).sessions.find((s) => s.id === fresh.id)?.panes[0]?.agentType === "claude", 10000);
          check("the pane wears the Claude badge", named);
        }
        shot("c-project-tab");
      }
    }
  }

  // --- d. codex pane ---------------------------------------------------------
  if (section("d", "codex: a codex tab runs the shim, reports agent=codex, writes the hooks profile")) {
    if (!projectId) {
      await send({ verb: "project.add", path: repoDir });
      const projectsJson = path.join(perchDir, "projects.json");
      await waitUntil(() => fs.existsSync(projectsJson) && fs.readFileSync(projectsJson, "utf8").includes("e2e-repo"), 10000);
      projectId = String(projectByPath(projectsJson).id);
    }
    const before = await dump();
    const agentsBefore = count("type=agent");
    await send({ verb: "project.tab.new", projectId, name: "codex tab", agent: "codex", worktree: false });
    const fresh = await (async () => {
      let f = null;
      await waitUntil(async () => { const d = await dump(); f = d.sessions.find((s) => !before.sessions.some((b) => b.id === s.id)); return !!f; }, 20000);
      return f;
    })();
    check("a codex tab appeared", !!fresh);
    if (fresh) {
      const paneId = fresh.panes[0].id;
      check("the shim reported agent=codex over the pane pipe",
        await waitUntil(() => count("type=agent") > agentsBefore && last(`pane=${paneN(paneId)} type=agent`) !== "", 30000));
      const badge = await waitUntil(async () => (await dump()).sessions.find((s) => s.id === fresh.id)?.panes[0]?.agentType === "codex", 10000);
      check("the pane wears the codex badge (agentType=codex)", badge);
      const profile = path.join(codexHome, "perch.config.toml");
      const wrote = await waitUntil(() => fs.existsSync(profile), 10000);
      check("the wrapper wrote perch.config.toml into CODEX_HOME", wrote);
      if (wrote) {
        const toml = fs.readFileSync(profile, "utf8");
        check("the profile's hooks call the bundled perch", toml.includes("hooks codex stop") && toml.includes(path.join(toolsDir, "perch")),
          `- ${toml.split("\n").find((l) => l.includes("command")) ?? ""}`);
        check("hook commands are unquoted paths (codex execs argv[0] itself)", !/command = '"/.test(toml));
      }
      shot("d-codex-tab");
    }
  }

  // --- e. the team room --------------------------------------------------------
  const roomOn = ["e1", "e2", "e3", "e4", "e5"].some((s) => sections.includes(s));
  if (roomOn && !projectId) {
    await send({ verb: "project.add", path: repoDir });
    const projectsJson = path.join(perchDir, "projects.json");
    await waitUntil(() => fs.existsSync(projectsJson) && fs.readFileSync(projectsJson, "utf8").includes("e2e-repo"), 10000);
    projectId = String(projectByPath(projectsJson).id);
  }

  if (section("e1", "room, warm: a post reaches a running bot and its answer comes back")) {
    const up = await bringUpBot(projectId, "Ada", "ada");
    check("Ada's Claude reported its session", up);
    if (!up) throw new Error("the bot never started; the room sections cannot run");
    await sleep(6000); // let the TUI paint before typing into it
    const before = count("Team.deliver");
    await send({ verb: "team.post", projectId, text: "Reply with exactly this word and nothing else: PONGONE", to: ["Ada"], clientId: "c1" });
    check("the post was typed into the bot", await waitUntil(() => count("Team.deliver") > before, 20000));
    check("the prompt-submit hook confirmed it", await waitUntil(() => /confirmed/.test(last("Team.submit")), 30000));
    check("the bot's answer came back into the room", await waitUntil(async () => { await answerCards(projectId); return ledgerHas(await teamDump(projectId), saysWord("PONGONE")); }, 180000, 3000));
    check("no 'didn't take the post' row", count("Team.submit") >= 1 && count("gave up") === 0);
    shot("e1-warm");
  }

  let coldPost = null;
  if (section("e2", "room, cold: after a restart the post waits for the bot's Claude")) {
    // The bot's tab comes back, but nothing spawns its terminal until someone
    // opens it — the state two of the owner's posts were lost in.
    await relaunchAndWait();
    // The launch prompt is deliberately NOT answered: that is what holds
    // every restored pane's spawn, so the bot's tab has no terminal at all.
    const deliverBefore = count("Team.deliver");
    const sessionsBefore = count("type=session");
    const startNeededBefore = count("Team.start.needed");
    await send({ verb: "team.post", projectId, text: "Reply with exactly this word and nothing else: PONGTWO", to: ["Ada"], clientId: "c2" });
    check("Perch started the bot's terminal for the post", await waitUntil(() => count("Team.start.needed") > startNeededBefore, 15000));
    await sleep(4000);
    const typedEarly = count("Team.deliver") > deliverBefore;
    const claudeUpEarly = count("type=session") > sessionsBefore;
    check("nothing was typed into the pane while it was starting", !typedEarly || claudeUpEarly, "- Team.deliver fired before the session hook");
    // The restored folder is already trusted, but if Claude asks again the
    // room raises the card and this answers it.
    const came = await waitUntil(async () => {
      if (count("type=session") > sessionsBefore) return true;
      if (count("Team.trust.ask") > 0 && last("Team.trust.ask").includes("bot=ada") && count("Team.trust.answer") === 0)
        await send({ verb: "team.bot.answer", projectId, botId: "ada", answer: "trust" });
      return false;
    }, 120000);
    check("the bot's Claude came up", came);
    check("the parked post was delivered after that", await waitUntil(() => count("Team.deliver") > deliverBefore, 30000));
    check("the woken bot answered in the room", await waitUntil(async () => { await answerCards(projectId); return ledgerHas(await teamDump(projectId), saysWord("PONGTWO")); }, 180000, 3000));
    const d = await teamDump(projectId);
    coldPost = (d.ledger ?? []).find((e) => e.kind === "user" && String(e.text).includes("PONGTWO"));
    const mark = (d.ledger ?? []).filter((e) => e.event === "delivered" && coldPost && String(e.note) === String(coldPost.seq));
    check("the room marks that post as delivered", !!coldPost && mark.length >= 1);
    check("the room never said the bot didn't take it", !(d.ledger ?? []).some((e) => e.event === "undelivered"));
    shot("e2-cold");
  }

  if (section("e3", "room: Send again re-types the same line and adds no second post")) {
    const d = await teamDump(projectId);
    const post = coldPost ?? (d.ledger ?? []).filter((e) => e.kind === "user").pop();
    const postsBefore = (d.ledger ?? []).filter((e) => e.kind === "user").length;
    await send({ verb: "team.deliver.retry", projectId, seq: Number(post.seq), botId: "ada" });
    check("the host typed it again", await waitUntil(() => /ok=True/.test(last("Team.retry")), 20000));
    const d2 = await teamDump(projectId);
    check("no second post appeared in the room", (d2.ledger ?? []).filter((e) => e.kind === "user").length === postsBefore);
  }

  if (section("e4", "room: a permission card answered 15 seconds later still runs the command")) {
    fs.mkdirSync(path.join(repoDir, ".claude"), { recursive: true });
    fs.writeFileSync(path.join(repoDir, ".claude/settings.json"), JSON.stringify({ permissions: { ask: ["Bash(git tag:*)"] } }));
    await sleep(2000);
    const askBefore = count("Team.perm.ask");
    await send({ verb: "team.post", projectId, text: "Run this exact command with the Bash tool: git tag -l perm-check. Then post the word TAGDONE to the room.", to: ["Ada"], clientId: "c3" });
    const carded = await waitUntil(() => count("Team.perm.ask") > askBefore, 120000);
    check("the room raised a permission card", carded);
    if (carded) {
      let first = true;
      let ran = false;
      const end = Date.now() + 240000;
      while (Date.now() < end) {
        for (const l of logLines()) {
          if (!l.includes("Team.perm.ask")) continue;
          const m = /id=([0-9a-f]+)/.exec(l);
          if (!m || answeredCards.has(m[1])) continue;
          answeredCards.add(m[1]);
          if (first) { first = false; console.log(`      card ${m[1]} — waiting 15 s before answering, as a person would`); await sleep(15000); }
          await send({ verb: "team.perm.answer", projectId, id: m[1], decision: "allow" });
        }
        if (ledgerHas(await teamDump(projectId), saysWord("TAGDONE"))) { ran = true; break; }
        await sleep(3000);
      }
      check("the late Allow settled the prompt (the command ran)", ran);
    }
  }

  if (section("e5", "room: a message from one bot to another actually reaches it")) {
    const bo = await bringUpBot(projectId, "Bo", "bo");
    check("Bo's Claude reported its session", bo);
    await sleep(6000);
    await send({ verb: "team.post", projectId, text: "Use your SendMessage tool to send bo exactly this: PEERONE. Nothing else.", to: ["Ada"], clientId: "p1" });
    check("the room recorded Ada's message to Bo", await waitUntil(async () => { await answerCards(projectId); return ledgerHas(await teamDump(projectId), (e) => e.kind === "peer" && String(e.text).includes("PEERONE") && e.ok === true); }, 150000, 3000));
    check("Bo acted on the message while idle", await waitUntil(async () => { await answerCards(projectId); return ledgerHas(await teamDump(projectId), (e) => e.from === "Bo" && String(e.text).includes("PEERONE")); }, 180000, 3000));
    await send({ verb: "team.post", projectId, text: "Count slowly from 1 to 12, one number per line, then post the word COUNTED to the room.", to: ["Bo"], clientId: "p2" });
    await sleep(3000);
    await send({ verb: "team.post", projectId, text: "Use your SendMessage tool to send bo exactly this: PEERTWO. Nothing else.", to: ["Ada"], clientId: "p3" });
    check("the room recorded the second message", await waitUntil(async () => { await answerCards(projectId); return ledgerHas(await teamDump(projectId), (e) => e.kind === "peer" && String(e.text).includes("PEERTWO") && e.ok === true); }, 150000, 3000));
    check("Bo acted on it although it was mid-turn", await waitUntil(async () => { await answerCards(projectId); return ledgerHas(await teamDump(projectId), (e) => e.from === "Bo" && String(e.text).includes("PEERTWO")); }, 240000, 3000));
    check("no send failed on an ambiguous name", count("Team.peer.failed") === 0);
    shot("e5-bots");
  }

  // --- the log, at the end -------------------------------------------------
  console.log("\n[log] no ERROR lines across the run");
  const errs = errorLines();
  check("zero ERROR lines", errs.length === 0, `- ${errs.length} line(s), first: ${(errs[0] ?? "").slice(0, 160)}`);

  console.log("");
  const secs = Math.round((Date.now() - t0) / 1000);
  if (fails.length) {
    console.log(`MAC E2E FAILED (${fails.length}/${checks} checks, ${secs}s): ${fails.join(" | ")}`);
    console.log(`Do not release. The log is at ${logPath}`);
    exitCode = fails.length;
  } else {
    console.log(`MAC E2E PASSED — ${checks} checks in ${secs}s. Claude starts from a Dock launch, the shims run, the room reaches its bots.`);
  }
} catch (e) {
  console.log(`ERROR: ${e.message ?? e}`);
  if (fs.existsSync(logPath)) console.log(`log: ${logPath}`);
  exitCode = exitCode || 1;
} finally {
  await stop();
  fs.writeFileSync(path.join(outDir, "results.json"), JSON.stringify({ fails, checks, dataDir }, null, 2));
  if (exitCode === 0) fs.rmSync(dataDir, { recursive: true, force: true });
  else console.log(`data dir kept for inspection: ${dataDir}`);
}
process.exit(exitCode);
