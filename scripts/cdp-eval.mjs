// Evaluate one expression in the running app's page and print the result.
//
// The generic escape hatch for asserting on the REAL DOM from a PowerShell
// harness. Screenshots can't be trusted in this app (PrintWindow crops at
// non-100% DPI and misses GPU-composited layers; CDP captures composite
// transparency as white -- see CLAUDE.md), so when a test needs to know what is
// actually on screen, it asks the DOM.
//
//   usage: node cdp-eval.mjs "<js expression>" [timeoutMs] [port]
//   prints: the JSON-serialized value, or "ERR: <message>" on failure (exit 1)
//
// The app must have been launched with
//   WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>
// Debug builds only -- release builds disable DevTools.

const [, , expr, timeoutArg, portArg] = process.argv;
if (!expr) {
  console.error("usage: cdp-eval.mjs \"<js expression>\" [timeoutMs] [port]");
  process.exit(2);
}
const TIMEOUT = Number(timeoutArg ?? 15000);
const PORT = Number(portArg ?? 9333);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findPageWsUrl(deadline) {
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const targets = await res.json();
      // The app shell, not a URL pane: each browser pane is its own CDP target
      // and matching one of those would evaluate against the wrong document.
      const t = targets.find(
        (x) => x.type === "page" && x.url.startsWith("https://perch.local")
      );
      if (t) return t.webSocketDebuggerUrl;
    } catch { /* browser not up yet */ }
    await sleep(300);
  }
  throw new Error(`no perch.local CDP target on port ${PORT}`);
}

async function main() {
  const deadline = Date.now() + TIMEOUT;
  const wsUrl = await findPageWsUrl(deadline);
  const ws = new WebSocket(wsUrl);
  await new Promise((res, rej) => {
    ws.onopen = res;
    ws.onerror = () => rej(new Error("CDP websocket failed to open"));
  });
  const result = await new Promise((res, rej) => {
    const timer = setTimeout(() => rej(new Error("evaluate timed out")), Math.max(1000, deadline - Date.now()));
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id !== 1) return;
      clearTimeout(timer);
      if (m.error) return rej(new Error(m.error.message));
      if (m.result?.exceptionDetails) {
        return rej(new Error(m.result.exceptionDetails.exception?.description ?? "page threw"));
      }
      res(m.result?.result?.value);
    };
    ws.send(JSON.stringify({
      id: 1,
      method: "Runtime.evaluate",
      params: { expression: expr, returnByValue: true, awaitPromise: true },
    }));
  });
  ws.close();
  console.log(JSON.stringify(result));
}

main().catch((err) => {
  console.log("ERR: " + err.message);
  process.exit(1);
});
