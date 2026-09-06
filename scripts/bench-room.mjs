// Measures what the team room costs while several bots work.
//
// The room felt heavy and its animations laggy with multiple bots busy. This
// puts numbers on it before anything is changed: how many bot faces are being
// animated, how long a frame of that animation takes, and how long one feed
// render takes on a full ledger (the host sends up to 500 entries).
//
//   usage: node scripts/bench-room.mjs [port]
//
// Runs against the design harness, so it needs no tokens and no real bots.

import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const PORT = Number(process.argv[2] ?? 9445);
const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, "..");
const PAGE = "file:///" + join(ROOT, "design-loop", "harness.html").replace(/\\/g, "/") + "#team";

const BROWSERS = [
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
  "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findPage(deadline) {
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const t = (await res.json()).find((x) => x.type === "page" && x.url.includes("harness.html"));
      if (t) return t.webSocketDebuggerUrl;
    } catch { /* not up */ }
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
      setTimeout(() => { if (waiting.delete(n)) rej(new Error("evaluate timed out")); }, 30000);
      ws.send(JSON.stringify({
        id: n, method: "Runtime.evaluate",
        params: { expression, returnByValue: true, awaitPromise: true },
      }));
    });
  return { open, evaluate, close: () => ws.close() };
}

const browser = BROWSERS.find(existsSync);
if (!browser) { console.error("no Edge/Chrome found"); process.exit(2); }

const child = spawn(browser, [
  // GPU left ON: the app runs in WebView2 with hardware rasterization, and a
  // software-rendered measurement blames paint for costs the real app does not
  // pay. --disable-gpu here made every number three times too big.
  "--headless=new", "--window-size=1400,900",
  `--remote-debugging-port=${PORT}`,
  "--user-data-dir=" + join(ROOT, ".cdp-bench-profile"),
  PAGE,
], { stdio: "ignore" });

let cdp = null;
try {
  cdp = connect(await findPage(Date.now() + 20000));
  await cdp.open;
  await sleep(1500);

  const bench = `(async () => {
    const h = window.__perchHarness;
    if (!h?.stress) return JSON.stringify({ error: "harness has no stress hook" });
    const out = {};

    // CONTROL first: the same page with a small room. Anything the big room
    // costs on top of this is the room's doing, not the browser's.
    const frameOf = async () => {
      const f = [];
      await new Promise((done) => {
        let n = 0, last = performance.now();
        const step = (t) => { f.push(t - last); last = t; if (++n >= 40) return done(); requestAnimationFrame(step); };
        requestAnimationFrame(step);
      });
      f.shift(); f.sort((a, b) => a - b);
      return +f[Math.floor(f.length / 2)].toFixed(2);
    };
    out.frameSmallMs = await frameOf();

    // Fill the room the way a busy team does: the host ships up to 500
    // entries, and every message row from a bot carries an animated face.
    out.grow = await h.stress.fill(400);

    // Let the visibility observer settle before counting anything.
    await new Promise((r) => setTimeout(r, 600));

    const faces = document.querySelectorAll("svg.bot-face").length;
    const onScreen = [...document.querySelectorAll("svg.bot-face")].filter((s) => {
      const r = s.getBoundingClientRect();
      return r.bottom > 0 && r.top < window.innerHeight && r.width > 0;
    }).length;
    out.faces = faces;
    out.facesOnScreen = onScreen;

    // One animation frame's cost, measured over many frames.
    const frames = [];
    await new Promise((done) => {
      let n = 0, last = performance.now();
      const step = (t) => {
        frames.push(t - last); last = t;
        if (++n >= 60) return done();
        requestAnimationFrame(step);
      };
      requestAnimationFrame(step);
    });
    frames.shift();
    frames.sort((a, b) => a - b);
    out.frameMedianMs = +frames[Math.floor(frames.length / 2)].toFixed(2);
    out.frameWorstMs = +frames[frames.length - 1].toFixed(2);

    // Faces frozen: if the frame stays slow with every face stopped, the
    // faces are innocent and the cost is elsewhere in the page.
    h.stress.freeze();
    await new Promise((r) => setTimeout(r, 200));
    out.frameFrozenMs = await frameOf();
    h.stress.thaw();

    // Split the frame: how much is the faces' own JS, and how much is
    // everything else the browser does with this page.
    out.faceJsMs = +(await h.stress.timeFaceFrame()).toFixed(2);
    const st = h.stress.faceStats();
    out.drawnPerFrame = st.drawn;
    out.faceLoopMs = +st.ms.toFixed(2);

    // One feed render on that full ledger — what every new activity row costs.
    out.renderMs = +(await h.stress.timeRender()).toFixed(2);
    out.rows = document.querySelectorAll("#team-feed > *, .team-feed > *").length;
    return JSON.stringify(out);
  })()`;

  const r = JSON.parse(await cdp.evaluate(bench));
  if (r.error) { console.log("bench failed: " + r.error); process.exit(1); }
  console.log("\nTeam room under load (400 entries, 3 bots working)");
  console.log(`  animated bot faces        ${r.faces}   (only ${r.facesOnScreen} are on screen)`);
  console.log(`  frame in a SMALL room     ${r.frameSmallMs} ms   (the control)`);
  console.log(`  animation frame, median   ${r.frameMedianMs} ms   (16.7 ms = 60fps)`);
  console.log(`  animation frame, worst    ${r.frameWorstMs} ms`);
  console.log(`  faces drawn per frame     ${r.drawnPerFrame} of ${r.faces}`);
  console.log(`  the face loop itself      ${r.faceLoopMs} ms`);
  console.log(`  frame with faces FROZEN   ${r.frameFrozenMs} ms   (are the faces to blame?)`);
  console.log(`  feed rows in the DOM      ${r.rows}`);
  console.log(`  one feed render           ${r.renderMs} ms   (runs on every new activity row)`);
  console.log("");

  // Budgets, deliberately loose - this guards against the ROOM getting heavy
  // again, not against a busy build machine. Before the fix these read 315
  // faces drawn, 150 ms a frame and 66 ms a render, so anything near the
  // limits below is already a different bug.
  let bad = 0;
  const budget = (name, got, max, unit) => {
    const ok = got <= max;
    console.log(`  ${ok ? "PASS" : "FAIL"}  ${name}: ${got}${unit} (budget ${max}${unit})`);
    if (!ok) bad++;
  };
  budget("faces animating at once", r.drawnPerFrame, 12, "");
  budget("animation frame", r.frameMedianMs, 25, " ms");
  budget("one feed render", r.renderMs, 20, " ms");
  console.log("");
  if (bad > 0) { console.log(`${bad} budget(s) blown - the room got heavy again.`); process.exitCode = 1; }
  else console.log("Room stays light with a full ledger and three bots working.");
} catch (err) {
  console.log("bench failed: " + err.message);
  process.exitCode = 1;
} finally {
  cdp?.close();
  child.kill();
}
