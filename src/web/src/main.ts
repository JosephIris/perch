// Stage 2: one xterm.js Terminal bridged to a real ConPTY-backed shell.
//
// Wire:
//   bridge.send('ready')   <- on load, asks the host to spawn the shell
//   host -> 'pty.out'      -> term.write(bytes)
//   term.onData()          -> bridge.send('pty.in', { b64 })
//   ResizeObserver / fit() -> bridge.send('pty.resize', { cols, rows })
//   host -> 'pty.exit'     -> render a banner, dim the terminal

import "./style.css";

import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { Unicode11Addon } from "@xterm/addon-unicode11";

// ---- Host bridge -----------------------------------------------------------

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(msg: unknown): void;
        addEventListener(type: "message", listener: (e: MessageEvent) => void): void;
      };
    };
  }
}

interface HostMessage {
  type: string;
  [k: string]: unknown;
}

const bridge = {
  send(type: string, payload: Record<string, unknown> = {}) {
    const msg = JSON.stringify({ type, ...payload });
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(msg);
    } else {
      console.log("[bridge.no-host]", msg);
    }
  },
};

// ---- Status helpers --------------------------------------------------------

const statusEl = document.getElementById("status-text");
function setStatus(text: string) {
  if (statusEl) statusEl.textContent = text;
}

// ---- Base64 helpers --------------------------------------------------------
// Round-trip bytes byte-for-byte. We avoid TextEncoder/UTF-8 because the PTY
// can emit partial multi-byte sequences (e.g. an emoji split across reads)
// and xterm.js's own VT parser is happiest reconstructing them.

function b64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function bytesToB64(buf: Uint8Array): string {
  // Chunk to avoid Function.apply argument limits on large blobs.
  const CHUNK = 0x8000;
  let s = "";
  for (let i = 0; i < buf.length; i += CHUNK) {
    s += String.fromCharCode(...buf.subarray(i, i + CHUNK));
  }
  return btoa(s);
}

const utf8Enc = new TextEncoder();

// ---- Terminal --------------------------------------------------------------

const host = document.getElementById("terminal-host");
if (!host) throw new Error("terminal-host element missing");

const term = new Terminal({
  fontFamily: 'Cascadia Mono, "Cascadia Code", Consolas, monospace',
  fontSize: 13,
  lineHeight: 1.2,
  cursorBlink: true,
  cursorStyle: "block",
  allowProposedApi: true,
  scrollback: 10_000,
  theme: {
    background: "#0c0c0c",
    foreground: "#cdd6f4",
    cursor: "#cdd6f4",
    selectionBackground: "rgba(118, 185, 237, 0.3)",
  },
});

const fit = new FitAddon();
term.loadAddon(fit);
term.loadAddon(new WebLinksAddon());
term.loadAddon(new Unicode11Addon());
term.unicode.activeVersion = "11";

term.open(host);
// Smoke write: lets us tell "xterm doesn't render at all" apart from
// "host bytes didn't arrive" while we stabilize stage 2.
term.writeln("\x1b[2m[xterm.js ready]\x1b[0m");

// WebGL renderer temporarily off while we debug a blank-canvas issue.
// Re-enable once stage 2 is verified.
// try {
//   const webgl = new WebglAddon();
//   webgl.onContextLoss(() => webgl.dispose());
//   term.loadAddon(webgl);
// } catch (err) {
//   console.warn("[xterm] WebGL renderer unavailable, falling back to canvas:", err);
// }

// ---- Sizing ----------------------------------------------------------------
// Debounce resize: ResizeObserver can fire many times during a window drag.
// We send at most one resize per animation frame.

let pendingResize = false;
function reportResize() {
  if (pendingResize) return;
  pendingResize = true;
  requestAnimationFrame(() => {
    pendingResize = false;
    try { fit.fit(); } catch { return; }
    bridge.send("pty.resize", { cols: term.cols, rows: term.rows });
  });
}

const ro = new ResizeObserver(reportResize);
ro.observe(host);

// ---- Input -> host ---------------------------------------------------------

term.onData((data: string) => {
  // xterm.js gives us strings; encode to UTF-8 bytes for the PTY.
  const bytes = utf8Enc.encode(data);
  bridge.send("pty.in", { b64: bytesToB64(bytes) });
});

// ---- Host -> output --------------------------------------------------------

function handleHostMessage(raw: unknown) {
  // PostWebMessageAsJson delivers a parsed object; PostWebMessageAsString
  // delivers a string. Tolerate both so the JS side isn't coupled to the
  // host's choice of API.
  let msg: HostMessage;
  if (typeof raw === "string") {
    try { msg = JSON.parse(raw) as HostMessage; }
    catch { console.warn("[host msg] non-JSON string:", raw); return; }
  } else if (raw && typeof raw === "object") {
    msg = raw as HostMessage;
  } else {
    return;
  }
  switch (msg.type) {
    case "pty.out": {
      const b64 = msg.b64 as string | undefined;
      if (!b64) return;
      const bytes = b64ToBytes(b64);
      term.write(bytes);
      if (statusEl?.textContent === "connecting to shell...") {
        setStatus(`ready  (pid ${msg.pid ?? "?"})`);
      }
      break;
    }
    case "pty.exit": {
      const code = msg.code as number;
      term.writeln(`\r\n\x1b[2m[shell exited with code ${code}]\x1b[0m`);
      setStatus(`exited (${code})`);
      break;
    }
    case "host.error": {
      const message = String(msg.message ?? "");
      term.writeln(`\r\n\x1b[31m[host error]\x1b[0m ${message}`);
      setStatus("error");
      break;
    }
  }
}

if (window.chrome?.webview) {
  window.chrome.webview.addEventListener("message", (e: MessageEvent) => {
    handleHostMessage(e.data);
  });
  console.log("[bridge] host listener bound");
} else {
  console.warn("[bridge] no chrome.webview -- running in plain browser");
}

// ---- Kickoff ---------------------------------------------------------------
// Tell the host to spawn the shell. The host will respond with pty.out
// once the shell prints its prompt.
setStatus("connecting to shell...");
bridge.send("ready", { stage: 2 });

// Make sure we report initial size after the first layout pass; the
// ResizeObserver does this already but reportResize() also ensures the
// host gets a defined size before any output arrives.
queueMicrotask(reportResize);
