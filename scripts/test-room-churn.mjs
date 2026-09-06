// Proves the team room stops rebuilding itself under a working bot.
//
// The defect: render() runs on EVERY state push, and while bots work those
// arrive many times a second (state, elapsed, activity detail). The roster,
// task column and artefact panel each did an unconditional replaceChildren()
// on every one of them. So a bot's animated face was re-parented several times
// a second, the hover under the cursor was dropped, and the artefact you were
// reading was re-rendered from the top. That churn was the room's flicker.
//
// The feed already had reuse guards and said why in a comment. This asserts
// the other three now do too, by DOM NODE IDENTITY: push state repeatedly with
// nothing meaningful changed and check the same element objects are still
// there. It fails loudly on the old build.
//
//   usage: node scripts/test-room-churn.mjs [port]
//
// Runs against the DESIGN HARNESS (design-loop/harness.html#team), not the
// app: the harness has bots, tasks and an artefact without needing tokens or a
// real Claude. Launch a headless browser on it with --remote-debugging-port
// first, or let this script do it.

import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const PORT = Number(process.argv[2] ?? 9444);
const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, "..");
const PAGE = "file:///" + join(ROOT, "design-loop", "harness.html").replace(/\\/g, "/") + "#team";

const EDGES = [
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
  "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let fails = 0;
const check = (name, ok, detail = "") => {
  console.log(`  ${ok ? "PASS" : "FAIL"}  ${name}${ok || !detail ? "" : "  " + detail}`);
  if (!ok) fails++;
};

async function findPage(deadline) {
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const t = (await res.json()).find((x) => x.type === "page" && x.url.includes("harness.html"));
      if (t) return t.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(300);
  }
  throw new Error("harness page never appeared on CDP");
}

function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let id = 0;
  const waiting = new Map();
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    const w = waiting.get(m.id);
    if (!w) return;
    waiting.delete(m.id);
    if (m.error) return w.rej(new Error(m.error.message));
    if (m.result?.exceptionDetails)
      return w.rej(new Error(m.result.exceptionDetails.exception?.description ?? "page threw"));
    w.res(m.result?.result?.value);
  };
  const open = new Promise((res, rej) => {
    ws.onopen = res;
    ws.onerror = () => rej(new Error("CDP websocket failed to open"));
  });
  const evaluate = (expression) =>
    new Promise((res, rej) => {
      const n = ++id;
      waiting.set(n, { res, rej });
      setTimeout(() => { if (waiting.delete(n)) rej(new Error("evaluate timed out")); }, 15000);
      ws.send(JSON.stringify({
        id: n, method: "Runtime.evaluate",
        params: { expression, returnByValue: true, awaitPromise: true },
      }));
    });
  return { open, evaluate, close: () => ws.close() };
}

const browser = EDGES.find(existsSync);
if (!browser) { console.error("no Edge/Chrome found"); process.exit(2); }

const child = spawn(browser, [
  "--headless=new", "--disable-gpu", "--window-size=1400,900",
  `--remote-debugging-port=${PORT}`,
  "--user-data-dir=" + join(ROOT, ".cdp-churn-profile"),
  PAGE,
], { stdio: "ignore" });

let cdp = null;
try {
  cdp = connect(await findPage(Date.now() + 20000));
  await cdp.open;
  await sleep(1500);   // the harness renders the room on load

  // Every push carries a DIFFERENT elapsed clock, exactly as a working bot's
  // does. Nothing a card or a roster row is supposed to redraw for.
  const churn = `(async () => {
    const h = window.__perchHarness;
    if (!h) return "no harness hook";
    const ids = (sel) => [...document.querySelectorAll(sel)];
    const before = {
      roster: ids(".roster-bot"),
      tasks:  ids(".task-card"),
      arte:   ids(".team-arte__doc"),
    };
    if (before.roster.length === 0) return "no roster rows on screen";
    for (let i = 0; i < 25; i++) { h.pushState(); await new Promise(r => setTimeout(r, 8)); }
    const after = {
      roster: ids(".roster-bot"),
      tasks:  ids(".task-card"),
      arte:   ids(".team-arte__doc"),
    };
    const same = (a, b) => a.length > 0 && a.length === b.length && a.every((n, i) => n === b[i]);
    return JSON.stringify({
      rosterRows: before.roster.length,
      taskCards:  before.tasks.length,
      arteDocs:   before.arte.length,
      rosterKept: same(before.roster, after.roster),
      tasksKept:  before.tasks.length === 0 || same(before.tasks, after.tasks),
      arteKept:   before.arte.length === 0 || same(before.arte, after.arte),
    });
  })()`;

  console.log("\n25 state pushes with nothing meaningful changed:");
  const raw = await cdp.evaluate(churn);
  if (typeof raw === "string" && !raw.startsWith("{")) {
    console.log(`  FAIL  the harness could not be driven: ${raw}`);
    fails++;
  } else {
    const r = JSON.parse(raw);
    console.log(`  (${r.rosterRows} roster rows, ${r.taskCards} task cards, ${r.arteDocs} artefact body)`);
    check("the roster rows survive — faces are never re-parented", r.rosterKept,
      "this churn WAS the blink");
    check("the task cards survive — an open editor is not closed under you", r.tasksKept);
    check("the artefact body survives — the reader keeps their place", r.arteKept);
  }

  // And it must still redraw when something REALLY changes, or the guard is
  // just a freeze.
  console.log("\na real change still redraws:");
  const real = await cdp.evaluate(`(async () => {
    const h = window.__perchHarness;
    const first = () => document.querySelector(".roster-bot");
    const was = first();
    h.pushState({ bump: true });
    await new Promise(r => setTimeout(r, 60));
    return first() !== was;
  })()`);
  check("a bot changing state rebuilds its row", real === true, `got ${real}`);
} catch (err) {
  console.log(`  FAIL  ${err.message}`);
  fails++;
} finally {
  cdp?.close();
  child.kill();
}

console.log("");
if (fails === 0) console.log("Room churn checks passed.");
else console.log(`${fails} check(s) failed.`);
process.exit(fails === 0 ? 0 : 1);
