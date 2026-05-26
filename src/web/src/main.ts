// Stage 1: one xterm.js Terminal with local echo. No ConPTY yet.
//
// What this proves end-to-end:
//   * WebView2 loads the bundle via the virtual host mapping
//   * xterm.js renders, the WebGL renderer initializes
//   * Keyboard input reaches the page (no airspace problem -- the whole UI
//     is one HWND)
//   * The bridge from page -> host works (`bridge.send()` posts a JSON
//     message that lands in MainWindow.OnWebMessage)
//
// Next stages will wire onData -> host (ConPTY input) and host messages ->
// term.write (ConPTY output).

import "./style.css";

import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { Unicode11Addon } from "@xterm/addon-unicode11";

// ---- Host bridge -----------------------------------------------------------
// chrome.webview is the WebView2-injected handle. We wrap it so component
// code doesn't reach for a globally-injected name.

declare global {
  interface Window {
    chrome?: { webview?: { postMessage(msg: unknown): void } };
  }
}

const bridge = {
  send(type: string, payload: Record<string, unknown> = {}) {
    const msg = JSON.stringify({ type, ...payload });
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(msg);
    } else {
      // Running in a plain browser (e.g. devtools detached). Log so we can
      // still iterate UI without the host attached.
      console.log("[bridge.no-host]", msg);
    }
  },
};

// ---- Status bar helpers ----------------------------------------------------
const statusEl = document.getElementById("status-text");
function setStatus(text: string) {
  if (statusEl) statusEl.textContent = text;
}

// ---- Terminal --------------------------------------------------------------
const host = document.getElementById("terminal-host");
if (!host) throw new Error("terminal-host element missing");

const term = new Terminal({
  fontFamily: 'Cascadia Mono, "Cascadia Code", Consolas, monospace',
  fontSize: 13,
  lineHeight: 1.2,
  cursorBlink: true,
  cursorStyle: "block",
  allowProposedApi: true,        // needed for Unicode11
  scrollback: 10_000,
  theme: {
    background: "#0c0c0c",       // conhost-equivalent
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

// WebGL can fail on machines without enough GPU (RDP, some VMs). Try, fall
// back gracefully to the canvas renderer if not.
try {
  const webgl = new WebglAddon();
  webgl.onContextLoss(() => webgl.dispose());
  term.loadAddon(webgl);
} catch (err) {
  console.warn("[xterm] WebGL renderer unavailable, falling back to canvas:", err);
}

// Resize with the host element. ResizeObserver fires on the initial layout
// too, so fit() runs once before we need it.
const ro = new ResizeObserver(() => {
  try { fit.fit(); } catch { /* element not yet measured */ }
});
ro.observe(host);

// Local-echo loop until ConPTY is wired. Backspace = erase one char.
term.onData((data: string) => {
  for (const ch of data) {
    if (ch === "\r") term.write("\r\n");
    else if (ch === "\x7f") term.write("\b \b");
    else term.write(ch);
  }
});

// Banner + status so the user sees something real.
term.writeln("\x1b[1;36mcmux\x1b[0m (webview-rewrite, stage 1)");
term.writeln("xterm.js is live. ConPTY bridge: pending.");
term.writeln("Type to verify local echo. Splits / shells next commit.");
term.write("\r\n$ ");

setStatus("ready (local echo only)");
bridge.send("ready", { stage: 1 });
