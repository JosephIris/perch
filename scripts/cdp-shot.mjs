// Screenshot the running app's page over CDP and print the PNG as base64.
//
//   usage: node cdp-shot.mjs [port]
//
// Companion to cdp-eval.mjs. Kept separate because a screenshot payload is
// megabytes and cdp-eval's contract is "print a small JSON value".
//
// IMPORTANT: force an opaque page background BEFORE calling this. The workspace
// has no background of its own (Mica shows through from the WPF host), and
// Page.captureScreenshot composites transparency as WHITE — so white text and
// hairline borders vanish. See CLAUDE.md "Both capture methods lie".

const PORT = Number(process.argv[2] ?? 9333);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function findPageWsUrl(deadline) {
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
      const targets = await res.json();
      const t = targets.find((x) => x.type === "page" && x.url.startsWith("https://perch.local"));
      if (t) return t.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(300);
  }
  throw new Error(`no perch.local CDP target on port ${PORT}`);
}

async function main() {
  const wsUrl = await findPageWsUrl(Date.now() + 15000);
  const ws = new WebSocket(wsUrl);
  await new Promise((res, rej) => {
    ws.onopen = res;
    ws.onerror = () => rej(new Error("CDP websocket failed to open"));
  });
  const data = await new Promise((res, rej) => {
    const timer = setTimeout(() => rej(new Error("capture timed out")), 20000);
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id !== 1) return;
      clearTimeout(timer);
      if (m.error) return rej(new Error(m.error.message));
      res(m.result?.data);
    };
    ws.send(JSON.stringify({
      id: 1,
      method: "Page.captureScreenshot",
      params: { format: "png", captureBeyondViewport: false },
    }));
  });
  ws.close();
  if (!data) throw new Error("empty capture");
  console.log(data);
}

main().catch((err) => {
  console.log("ERR: " + err.message);
  process.exit(1);
});
