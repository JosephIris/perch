// Session-switch render check: after switching sessions, is the selected
// session's terminal actually on screen, painted, with nothing thrown in the
// page? Drives an ISOLATED Perch (its own data dir and WebView2 profile, CDP
// on 9333) through the real page — never the control pipe, whose fixed name is
// shared with the user's live Perch.
//
// Why it exists: dropping a hidden stage's WebGL renderer and re-creating it
// on return is timing-sensitive, and the one time it broke (addon-webgl's
// dispose throwing against the xterm core we ship) the symptom was a pane
// that painted nothing until the user clicked it twice. Pixels are the only
// honest witness, so this shoots the visible terminal after each switch and
// counts the pixels that differ from the terminal background.
//
//   usage: node scripts/verify-switch-render.mjs [switches] [Perch.exe]
//   exit code: number of failed switches (0 = green)
//
// Needs a Debug build (release builds disable DevTools/CDP):
//   dotnet build src/Perch -c Debug

import { spawn, spawnSync } from "node:child_process";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { inflateSync } from "node:zlib";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SWITCHES = Number(process.argv[2] ?? 8);
const exe = process.argv[3] ?? join(repo, "src/Perch/bin/Debug/net8.0-windows/win10-x64/Perch.exe");
// PERCH_CDP_PORT picks the DevTools port. The default is the one every other
// harness uses, so a run refuses to start if something already answers there:
// a second WebView2 cannot bind it, and this script would silently drive
// whichever instance did — another agent's gate run, or the user's own app.
const PORT = Number(process.env.PERCH_CDP_PORT ?? 9333);
const dataDir = join(tmpdir(), `perch-switch-${Date.now()}`);
const shots = join(dataDir, "shots");
mkdirSync(join(dataDir, "perch"), { recursive: true });
mkdirSync(shots, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

try {
  await fetch(`http://127.0.0.1:${PORT}/json/version`);
  console.error(`port ${PORT} already serves a DevTools endpoint — another Perch is being driven there. Set PERCH_CDP_PORT to a free port.`);
  process.exit(2);
} catch { /* nothing listening: ours to take */ }

// ---- launch with a scrubbed environment ---------------------------------------
// A Perch started from inside a Claude Code pane inherits CLAUDE_* / PERCH_*
// markers that would make its panes think they are nested sessions.
const env = {};
for (const [k, v] of Object.entries(process.env)) {
  if (/^CLAUDE/i.test(k) || k === "PERCH_PANE_ID" || k === "PERCH_PIPE" || k === "NO_COLOR") continue;
  env[k] = v;
}
env.PERCH_DATA_DIR = dataDir;
env.WEBVIEW2_USER_DATA_FOLDER = join(dataDir, "webview2");
env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = `--remote-debugging-port=${PORT}`;
const child = spawn(exe, [], { env, stdio: "ignore" });
console.log(`launched ${exe} pid=${child.pid} data=${dataDir}`);
// Kill only the PID we launched — never by name; the user's own Perch is open.
const killApp = () => { try { spawnSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" }); } catch { /* gone */ } };
process.on("exit", killApp);
process.on("SIGINT", () => { killApp(); process.exit(130); });

// ---- CDP -----------------------------------------------------------------------
async function findPageWsUrl() {
  for (let i = 0; i < 80; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const targets = await res.json();
      // The app shell, not a URL pane (each browser pane is its own target).
      const t = targets.find((x) => x.type === "page" && x.url.startsWith("https://perch.local"));
      if (t) return t.webSocketDebuggerUrl;
    } catch { /* browser not up yet */ }
    await sleep(500);
  }
  throw new Error("no perch.local CDP target after 40s");
}
class Cdp {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); this.events = []; }
  static async open(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    const c = new Cdp(ws);
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && c.pending.has(m.id)) {
        const { res, rej } = c.pending.get(m.id);
        c.pending.delete(m.id);
        m.error ? rej(new Error(m.error.message)) : res(m.result);
      } else if (m.method === "Runtime.consoleAPICalled") {
        const text = (m.params.args || []).map((a) => a.value ?? a.description ?? "").join(" ");
        c.events.push(`[console.${m.params.type}] ${text}`);
      } else if (m.method === "Runtime.exceptionThrown") {
        const d = m.params.exceptionDetails;
        c.events.push(`[exception] ${d.text} ${d.exception?.description ?? ""}`);
      }
    };
    return c;
  }
  cmd(method, params = {}) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((res, rej) => this.pending.set(id, { res, rej }));
  }
  async eval(expression) {
    const r = await this.cmd("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error("page eval failed: " + JSON.stringify(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text));
    return r.result?.value;
  }
}
async function waitFor(desc, fn, timeoutMs = 30000, everyMs = 200) {
  const t0 = Date.now();
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() - t0 > timeoutMs) throw new Error(`timeout waiting for: ${desc}`);
    await sleep(everyMs);
  }
}

// ---- PNG: enough of a decoder for Chromium's 8-bit RGBA screenshots ------------
function decodePng(buf) {
  let off = 8, w = 0, h = 0, bpp = 0;
  const idat = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString("ascii", off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === "IHDR") {
      w = data.readUInt32BE(0); h = data.readUInt32BE(4);
      const depth = data[8], color = data[9], interlace = data[12];
      if (depth !== 8 || interlace !== 0) throw new Error(`unsupported PNG depth=${depth} interlace=${interlace}`);
      bpp = { 2: 3, 6: 4 }[color];
      if (!bpp) throw new Error(`unsupported PNG color type ${color}`);
    } else if (type === "IDAT") idat.push(data);
    else if (type === "IEND") break;
    off += 12 + len;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const stride = w * bpp;
  const px = Buffer.alloc(h * stride);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < h; y++) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    const cur = px.subarray(y * stride, (y + 1) * stride);
    for (let i = 0; i < stride; i++) {
      const a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
      let x = line[i];
      switch (filter) {
        case 0: break;
        case 1: x += a; break;
        case 2: x += b; break;
        case 3: x += (a + b) >> 1; break;
        case 4: { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); x += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; break; }
        default: throw new Error(`bad PNG filter ${filter}`);
      }
      cur[i] = x & 255;
    }
    prev = cur;
  }
  return { w, h, bpp, px };
}
// Share of pixels that are not the terminal background (#1f1f1f, ±24).
function paintedFraction({ w, h, bpp, px }) {
  let n = 0;
  const total = w * h;
  for (let i = 0; i < total; i++) {
    const o = i * bpp;
    if (Math.abs(px[o] - 31) > 24 || Math.abs(px[o + 1] - 31) > 24 || Math.abs(px[o + 2] - 31) > 24) n++;
  }
  return total ? n / total : 0;
}

// ---- drive ----------------------------------------------------------------------
const cdp = await Cdp.open(await findPageWsUrl());
await cdp.cmd("Runtime.enable");
await cdp.cmd("Page.enable");

// Tap state pushes for session ids and pane trees; drop the first-run card.
await cdp.eval(`(() => {
  window.__lastState = null;
  window.chrome.webview.addEventListener("message", (e) => {
    try {
      const d = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
      if (d && d.type === "state") window.__lastState = d;
    } catch {}
  });
  document.querySelector(".onboarding-overlay")?.remove();
  return true;
})()`);
const post = (msg) => cdp.eval(`window.chrome.webview.postMessage(${JSON.stringify(JSON.stringify(msg))}), true`);
const sessions = () => cdp.eval(`(() => {
  const s = window.__lastState; if (!s) return null;
  const leaves = (n) => n.kind === "leaf" ? [n.paneId] : n.children.flatMap(leaves);
  return { active: s.activeSessionId, list: (s.sessions || []).map(x => ({ id: x.id, title: x.title, dormant: !!x.dormant, panes: leaves(x.rootPane) })) };
})()`);

// A fresh data dir seeds one session; a second comes from session.new. The
// page's CDP target exists before the host has wired its message handlers, so
// wait for the seeded session's terminal to be mounted (proof a state push
// has been handled end to end) before posting anything, and re-post if a slow
// start still swallows the first one.
await waitFor("first terminal mounted", () => cdp.eval(`(window.__perchTerms || []).length >= 1`), 90000, 300);
await sleep(500);
const twoLive = (s) => s && s.list.filter((x) => !x.dormant).length >= 2 ? s : null;
let st = twoLive(await sessions());
for (let attempt = 0; attempt < 3 && !st; attempt++) {
  await post({ type: "session.new" });
  st = await waitFor("two live sessions", async () => twoLive(await sessions()), 8000).catch(() => null);
}
if (!st) {
  console.error("no second live session after three session.new posts");
  console.error("page:", await cdp.eval(`JSON.stringify({ terms: window.__perchTerms?.length, state: !!window.__lastState, text: document.body.innerText.replace(/\\s+/g, " ").slice(0, 160) })`));
  for (const e of cdp.events.slice(-10)) console.error("  " + e.replace(/\s+/g, " ").slice(0, 220));
  process.exit(3);
}
const [A, B] = st.list.filter((s) => !s.dormant);
console.log(`A=${A.id.slice(0, 8)} "${A.title}" panes=${A.panes.length}  B=${B.id.slice(0, 8)} "${B.title}" panes=${B.panes.length}`);
await cdp.eval(`document.querySelector(".onboarding-overlay")?.remove(), true`);

const select = async (s) => {
  await post({ type: "session.select", id: s.id });
  await waitFor(`host active=${s.id.slice(0, 8)}`, async () => (await sessions())?.active === s.id, 10000, 100);
};

// Visit both so each has a mounted terminal, and fill them with bright text so
// a painted pane is unmistakable in pixels. Filling goes through xterm's own
// write, so it is independent of what the shell prints.
const FILL = Array.from({ length: 40 }, (_, i) => `\\r\\n\\x1b[1;37m${String(i).padStart(2, "0")} ██ THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG 0123456789 ██ ${"#".repeat(40)}`).join("");
for (const s of [A, B]) {
  await select(s);
  await sleep(1200);
  await cdp.eval(`(() => { for (const t of window.__perchTerms) { if (t.element && t.element.offsetParent) t.write("${FILL}"); } return true; })()`);
  await sleep(300);
}
// CDP composites transparency as white and Mica shows through the workspace;
// an opaque page background keeps the capture honest (see CLAUDE.md).
await cdp.eval(`document.documentElement.style.background = '#1f1f1f', true`);

// The visible terminal: which pane it belongs to, its renderer, its rect.
const probe = () => cdp.eval(`(() => {
  const t = window.__perchTerms.find(t => t.element && t.element.offsetParent);
  if (!t) return { none: true };
  const core = t._core, rs = core._renderService, r = rs._renderer.value;
  const scr = core.screenElement.getBoundingClientRect();
  const canvas = core.screenElement.querySelector("canvas");
  return {
    paneId: t.element.closest(".pane")?.dataset.paneId,
    renderer: r?.constructor?.name, rendererDisposed: r?._isDisposed === true, canvasConnected: !!canvas,
    paused: rs._isPaused, cols: t.cols, rows: t.rows,
    rect: { x: scr.x, y: scr.y, w: scr.width, h: scr.height },
  };
})()`);
const shoot = async (name, rect) => {
  const r = await cdp.cmd("Page.captureScreenshot", { format: "png", clip: { x: rect.x, y: rect.y, width: rect.w, height: rect.h, scale: 1 } });
  const buf = Buffer.from(r.data, "base64");
  writeFileSync(join(shots, `${name}.png`), buf);
  return paintedFraction(decodePng(buf));
};

let failures = 0;
for (let i = 0; i < SWITCHES; i++) {
  const target = i % 2 === 0 ? B : A;
  const tag = target === A ? "A" : "B";
  const evBefore = cdp.events.length;
  await select(target);
  await sleep(700); // past the 200 ms stage fade and the two-frame renderer hand-back
  const p = await probe();
  const painted = p.none ? 0 : await shoot(`switch-${String(i).padStart(2, "0")}-${tag}`, p.rect);
  const thrown = cdp.events.slice(evBefore).filter((e) => /\[exception\]|listener threw|\[console\.error\]/.test(e));
  const rightPane = !p.none && target.panes.includes(p.paneId);
  const ok = rightPane && painted >= 0.01 && thrown.length === 0;
  if (!ok) failures++;
  console.log(`${ok ? "OK  " : "FAIL"} switch ${i} -> ${tag}: painted=${(painted * 100).toFixed(1)}% pane=${rightPane ? "selected session's" : "WRONG (" + (p.paneId ?? "none") + ")"} renderer=${p.renderer}${p.rendererDisposed ? "(disposed)" : ""} canvas=${p.canvasConnected} thrown=${thrown.length}`);
  for (const e of thrown) console.log("     " + e.replace(/\s+/g, " ").slice(0, 220));
  if (!ok && !p.none) {
    // The reported symptom: a click "brings it back". Record what one click does.
    const cx = p.rect.x + p.rect.w / 2, cy = p.rect.y + p.rect.h / 2;
    await cdp.cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: cx, y: cy, button: "left", clickCount: 1 });
    await cdp.cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: cx, y: cy, button: "left", clickCount: 1 });
    await sleep(500);
    const p2 = await probe();
    const painted2 = p2.none ? 0 : await shoot(`switch-${String(i).padStart(2, "0")}-${tag}-after-click`, p2.rect);
    console.log(`     after one click: painted=${(painted2 * 100).toFixed(1)}% pane=${target.panes.includes(p2.paneId) ? "selected session's" : "still wrong"}`);
  }
}
console.log(`\n${failures === 0 ? "GREEN" : "RED"}: ${SWITCHES - failures}/${SWITCHES} switches showed the selected session painted, cleanly. Shots: ${shots}`);
killApp();
await sleep(800);
if (failures === 0) { try { rmSync(dataDir, { recursive: true, force: true }); } catch { /* WebView2 may still hold a file; tmp anyway */ } }
process.exit(failures);
