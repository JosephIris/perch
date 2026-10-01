// A Perch.Mac for testing the iPhone app (ios/): its own data dir, the phone
// link on (port 47810, a fixed token), a throwaway project with one Claude tab.
// Prints the pairing link (LINK ...) and stays up until killed; kills only the
// Perch it launched. Then, in ios/:
//   TEST_RUNNER_PERCH_PAIR_URL="<link>" xcodebuild test -scheme PerchRemote \
//     -destination "platform=iOS Simulator,name=iPhone 18 Pro"
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { execFileSync, spawn } from "node:child_process";

const repo = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..");
const appBin = path.join(repo, "src/Perch.Mac/bin/Debug/net8.0/Perch");

const dataDir = path.join(os.tmpdir(), "perch-phone-sim");
const perchDir = path.join(dataDir, "perch");
const logPath = path.join(perchDir, "errors.log");
const repoDir = path.join(dataDir, "phone-repo");
const sockPath = path.join(os.tmpdir(), "CoreFxPipe_perch\\control");
const TOKEN = "simtoken-0123456789abcdef";
const PORT = 47810;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const ESC = "\x1b";

fs.rmSync(dataDir, { recursive: true, force: true });
fs.mkdirSync(perchDir, { recursive: true });
fs.writeFileSync(path.join(perchDir, "settings.json"),
  JSON.stringify({ PhoneEnabled: true, PhonePort: PORT, PhoneToken: TOKEN }));
fs.mkdirSync(repoDir, { recursive: true });
execFileSync("git", ["init", "-q"], { cwd: repoDir });
fs.writeFileSync(path.join(repoDir, "README.md"), "# phone test repo\n");
fs.rmSync(sockPath, { force: true });

const env = { ...process.env };
for (const k of Object.keys(env)) if (k.startsWith("CLAUDE")) delete env[k];
env.DOTNET_ROOT = path.join(os.homedir(), ".dotnet");
env.PERCH_DATA_DIR = dataDir;
env.PERCH_ENABLE_TEST_IPC = "1";
const app = spawn(appBin, [], { cwd: path.dirname(appBin), env, stdio: "ignore" });
console.log(`Perch pid ${app.pid}`);
const bye = () => { try { app.kill("SIGTERM"); } catch {} process.exit(0); };
process.on("SIGTERM", bye);
process.on("SIGINT", bye);
app.on("exit", (c) => { console.log(`Perch exited ${c}`); process.exit(1); });

const send = (obj) => new Promise((res, rej) => {
  const c = net.connect(sockPath);
  c.on("connect", () => c.end(JSON.stringify(obj) + "\n", res));
  c.on("error", rej);
});
const lines = () => { try { return fs.readFileSync(logPath, "utf8").split("\n"); } catch { return []; } };
const count = (p) => lines().filter((l) => l.includes(p)).length;
const last = (p) => lines().reverse().find((l) => l.includes(p)) ?? "";
async function until(f, ms, step = 500) {
  const end = Date.now() + ms;
  while (Date.now() < end) { try { if (await f()) return true; } catch {} await sleep(step); }
  return false;
}
async function dump() {
  const b = count("STATE_DUMP");
  await send({ verb: "state.dump" });
  await until(() => count("STATE_DUMP") > b, 8000, 150);
  const l = last("STATE_DUMP");
  return JSON.parse(l.slice(l.indexOf("STATE_DUMP") + 10));
}
const paneN = (id) => id.replace(/-/g, "");
const plain = (t) => t.replace(/\x1b\][^\x07\x1b]*(\x07|\x1b\\)/g, "").replace(/\x1b\[[0-9;?<>=]*[ -\/]*[@-~]/g, "").replace(/\x1b./g, "").replace(/\s+/g, "");
async function tail(paneId) {
  const tag = `PTY_TAIL pane=${paneN(paneId)} `;
  const b = count(tag);
  await send({ verb: "pty.tail", paneId });
  if (!await until(() => count(tag) > b, 5000, 100)) return "";
  const l = last(tag);
  return Buffer.from(l.slice(l.indexOf(tag) + tag.length).trim(), "base64").toString("utf8");
}

if (!await until(() => fs.existsSync(sockPath), 30000)) { console.log("no control pipe"); bye(); }
await sleep(1500);
await send({ verb: "project.add", path: repoDir });
const pj = path.join(perchDir, "projects.json");
await until(() => fs.existsSync(pj) && fs.readFileSync(pj, "utf8").includes("phone-repo"), 10000);
const doc = JSON.parse(fs.readFileSync(pj, "utf8"));
const proj = (doc.Projects ?? doc.projects).find((p) => String(p.Path ?? p.path).includes("phone-repo"));
const before = await dump();
await send({ verb: "project.tab.new", projectId: String(proj.Id ?? proj.id), name: "phone test", agent: "claude", worktree: false });
let fresh;
await until(async () => (fresh = (await dump()).sessions.find((s) => !before.sessions.some((b) => b.id === s.id))), 30000);
const paneId = fresh.panes[0].id;
console.log(`tab ${fresh.id} pane ${paneId}`);
const up = await until(async () => {
  if (count(`pane=${paneN(paneId)} type=session`) > 0) return true;
  const t = plain(await tail(paneId));
  if (/safetycheck|trustthisfolder|Doyoutrust/i.test(t)) { console.log("answering trust prompt"); await send({ verb: "pty.send", paneId, text: `${ESC}[B\r` }); await sleep(4000); }
  return false;
}, 120000, 2000);
console.log(up ? "Claude is up" : "Claude did not report a session");

// --rich: what a real sidebar has, for the phone's list (no Claude turns):
// a project chat, and a second tab put to sleep.
if (process.argv.includes("--rich")) {
  const projectId = String(proj.Id ?? proj.id);
  await send({ verb: "projectchat.new", id: projectId, name: "phone-repo chat" });
  const beforeSleepy = await dump();
  await send({ verb: "project.tab.new", projectId, name: "sleepy tab", agent: "claude", worktree: false });
  let sleepy;
  await until(async () => (sleepy = (await dump()).sessions.find((s) => !beforeSleepy.sessions.some((b) => b.id === s.id))), 30000);
  if (sleepy) { await sleep(4000); await send({ verb: "session.dormant", id: sleepy.id }); console.log(`slept ${sleepy.id}`); }
}

const hello = await fetch(`http://127.0.0.1:${PORT}/v1/hello`, { headers: { Authorization: `Bearer ${TOKEN}` } }).then((r) => r.json());
console.log("hello", JSON.stringify(hello));
const hosts = [...(hello.hosts ?? []), "127.0.0.1"].join(",");
console.log(`LINK perch://pair?v=1&name=${encodeURIComponent(hello.name)}&port=${PORT}&token=${TOKEN}&hosts=${hosts}`);
setInterval(() => {}, 1 << 30);
