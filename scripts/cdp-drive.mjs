// CDP driver for the "prove the assumed flows" pass. Connects to the test
// instance's WebView2 over --remote-debugging-port and drives the REAL page:
// real keyboard shortcut -> real chooser dialog -> real button clicks, so the
// page itself authors the wire messages the typed host boundary consumes.
//
// usage: node cdp-drive.mjs <phaseA|phaseB> <dataDir>

import { connect } from "node:net";
import { readFileSync } from "node:fs";
import { join } from "node:path";

const PORT = 9333;
const [, , phase, dataDir] = process.argv;
if (!phase || !dataDir) { console.error("usage: cdp-drive.mjs <phaseA|phaseB> <dataDir>"); process.exit(2); }

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findPageWsUrl() {
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const targets = await res.json();
      const t = targets.find((x) => x.type === "page" && x.url.startsWith("https://perch.local"));
      if (t) return t.webSocketDebuggerUrl;
    } catch { /* browser not up yet */ }
    await sleep(500);
  }
  throw new Error("no perch.local CDP target after 30s");
}

class Cdp {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); }
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
    const r = await this.cmd("Runtime.evaluate", { expression, returnByValue: true });
    if (r.exceptionDetails) throw new Error("page eval failed: " + JSON.stringify(r.exceptionDetails.exception));
    return r.result?.value;
  }
}

async function waitFor(desc, fn, timeoutMs = 20000, everyMs = 300) {
  const t0 = Date.now();
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() - t0 > timeoutMs) throw new Error(`timeout waiting for: ${desc}`);
    await sleep(everyMs);
  }
}

function pipeSend(lines) {
  return new Promise((res, rej) => {
    const s = connect("\\\\.\\pipe\\perch\\control", () => {
      s.write(lines.map((l) => l + "\n").join(""));
      s.end(); res();
    });
    s.on("error", rej);
  });
}

const store = () => JSON.parse(readFileSync(join(dataDir, "perch", "sessions.json"), "utf8"));
const log = () => { try { return readFileSync(join(dataDir, "perch", "errors.log"), "utf8"); } catch { return ""; } };
const leaves = (n) => (n.Split == null ? [n] : n.Children.flatMap(leaves));
const ok = (msg) => console.log("OK " + msg);

const cdp = await Cdp.open(await findPageWsUrl());

// Fresh tap per step, installed right before the action that triggers the
// reply — a single long-lived tap proved fragile across the flow, and only
// low-volume KEEP types are stored (pane.out floods blow past CDP's
// returnByValue limit and the read comes back undefined).
const tapNew = (name) => cdp.eval(`(() => {
  window["${name}"] = [];
  const KEEP = new Set(["settings.data", "update.status", "commits.data", "restore.begin", "restore.progress", "restore.done"]);
  window.chrome.webview.addEventListener("message", (e) => {
    try {
      const d = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
      if (d && KEEP.has(d.type)) window["${name}"].push(JSON.stringify(d));
    } catch {}
  });
  return true;
})()`);
const rxHas = async (name, pred) => {
  const rx = (await cdp.eval(`window["${name}"] || []`)) ?? [];
  return rx.map((s) => { try { return JSON.parse(s); } catch { return {}; } }).find(pred);
};

if (phase === "phaseA") {
  // -- 1. New-pane chooser, end to end through the real page. Needs the source
  //       pane's cwd known first (OSC 7 from pwsh's first prompt).
  await waitFor("source pane cwd via OSC 7", () => leaves(store().Sessions[0].Root)[0].Cwd);
  const srcCwd = leaves(store().Sessions[0].Root)[0].Cwd;

  await cdp.eval(`window.dispatchEvent(new KeyboardEvent("keydown",
    { ctrlKey: true, shiftKey: true, code: "KeyD", key: "D", bubbles: true, cancelable: true })), true`);
  await waitFor("chooser dialog", () => cdp.eval(`!!document.querySelector(".pane-chooser__opt")`));
  ok("chooser: real Ctrl+Shift+D -> page sent pane.split -> host parked spawn -> chooser rendered");

  // Click option 2 = "Open a shell here" -> real page sends pane.chooser.choose.
  await cdp.eval(`document.querySelectorAll(".pane-chooser__opt")[1].click(), true`);
  await waitFor("chooser dismissed", async () => !(await cdp.eval(`!!document.querySelector(".pane-chooser__opt")`)));
  await waitFor("chosen pane spawned in source cwd", () => {
    const ls = leaves(store().Sessions[0].Root);
    return ls.length === 2 && ls[1].Cwd === srcCwd;
  });
  ok(`chooser: clicked "same" -> pane.chooser.choose -> spawned in ${srcCwd}`);

  // -- 2. URL pane: host-side split-with-url; the REAL page then measures the
  //       placeholder and emits urlpane.layout -> host creates the child WebView2.
  await pipeSend(['{"verb":"pane.split-active","dir":"right","url":"https://example.com"}']);
  await waitFor("UrlPane.create in host log", () => log().includes("UrlPane.create"));
  ok("urlpane: real page emitted urlpane.layout -> host created child WebView2");

  // -- 3. Settings dialog: the Check-now button being DISABLED on a dev build
  //       is itself proof of the settings.request -> settings.data round-trip
  //       (updatable:false came back through the typed path). Then prove the
  //       update.check boundary with the byte-identical wire the enabled
  //       button would send (settings.ts: send({ type: "update.check" })).
  await pipeSend(['{"verb":"ui.open-settings"}']);
  await waitFor("settings dialog", () => cdp.eval(`!!document.querySelector(".settings-card")`));
  const checkBtn = await waitFor("Check-now button state", () =>
    cdp.eval(`(() => {
      const b = [...document.querySelectorAll(".settings-btn")].find(x => x.textContent === "Check now");
      return b ? { disabled: b.disabled } : null;
    })()`));
  await tapNew("__updRx");
  if (checkBtn.disabled) {
    ok("settings: settings.request -> settings.data round-trip (dev build => Check-now disabled)");
    await cdp.eval(`window.chrome.webview.postMessage(JSON.stringify({ type: "update.check" })), true`);
  } else {
    await cdp.eval(`[...document.querySelectorAll(".settings-btn")].find(b => b.textContent === "Check now").click(), true`);
  }
  const status = await waitFor("update.status reply", () => rxHas("__updRx", (m) => m.type === "update.status"));
  if (status.state !== "unsupported") throw new Error(`expected update.status unsupported on a dev build, got ${status.state}`);
  ok("update: update.check -> host replied update.status=unsupported");
  await cdp.eval(`[...document.querySelectorAll(".settings-btn")].find(b => b.textContent === "Cancel")?.click(), true`);

  // -- 4. commits.request: byte-identical wire injection (real git state for the
  //       chip needs unpushed commits; this proves the boundary + reply path).
  const paneId = leaves(store().Sessions[0].Root)[0].Id;
  await tapNew("__comRx");
  await cdp.eval(`window.chrome.webview.postMessage(JSON.stringify({ type: "commits.request", paneId: "${paneId}" })), true`);
  const commits = await waitFor("commits.data reply", () => rxHas("__comRx", (m) => m.type === "commits.data" && m.paneId === paneId));
  if (commits.ahead !== 0) throw new Error(`expected ahead=0 in scratch cwd, got ${commits.ahead}`);
  ok("commits: commits.request round-trip -> commits.data (ahead=0)");

  // -- 5. Mock claude session so phase B can prove the resume dialog.
  await pipeSend(['{"verb":"pty.send","text":"claude\\r"}']);
  await waitFor("ClaudeSessionId captured from session-start hook", () =>
    leaves(store().Sessions[0].Root).some((l) => l.ClaudeSessionId), 30000);
  ok("mock claude session captured (ClaudeSessionId persisted) — ready for resume test");
  console.log("PHASE A PASS");
} else if (phase === "phaseB") {
  // Resume prompt: real dialog, real "Resume" click -> resume.decision -> armed
  // spawn with `claude --resume` -> restore lightbox completes.
  await waitFor("resume prompt dialog", () => cdp.eval(`!!document.querySelector(".confirm-card")`), 25000);
  ok("resume: launch prompt rendered (host posted resume.prompt)");

  await tapNew("__resRx");
  await cdp.eval(`document.querySelector(".confirm-card .settings-btn--accent").click(), true`);
  await waitFor("restore.done", () => rxHas("__resRx", (m) => m.type === "restore.done"), 30000);
  await waitFor("resumed spawn in log", () => log().includes("claude --resume mock-"), 10000);
  ok("resume: real Resume click -> resume.decision -> spawned `claude --resume <id>` -> restore.done");
  console.log("PHASE B PASS");
} else {
  throw new Error("unknown phase " + phase);
}
process.exit(0);
