// Only run against an explicitly launched, isolated Perch instance.
// Usage: node scripts/performance-live.mjs PORT OUTPUT_DIRECTORY
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import assert from 'node:assert/strict';
const port = Number(process.argv[2]);
const output = process.argv[3];
const baseline = process.argv.includes("--baseline");
if (!port || !output) throw new Error('Explicit isolated CDP port and output directory required');
const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const target = targets.find(t => t.url.startsWith('https://perch.local'));
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
let seq = 0;
const pending = new Map();
ws.onmessage = e => {
  const m = JSON.parse(e.data), p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
};
function call(method, params = {}) {
  return new Promise((resolve, reject) => { const id = ++seq; pending.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params })); });
}
async function evaluate(expression) {
  const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || 'Page evaluation failed');
  return result.result.value;
}
mkdirSync(output, { recursive: true });
try {
  const result = await evaluate(String.raw`(async () => {
    const wait = ms => new Promise(r => setTimeout(r, ms));
    const send = msg => chrome.webview.postMessage(JSON.stringify(msg));
    const term = () => window.__perchTerms.find(t => t.element?.closest('.workspace__stage')?.style.display !== 'none');
    const allText = t => Array.from({length:t.buffer.active.length}, (_,i) => t.buffer.active.getLine(i)?.translateToString(true) || '').join('\n');
    const firstId = window.__perch.state.activeSessionId;
    const firstPane = window.__perch.state.activePaneId;
    const echo = async marker => {
      const t = term(); t.focus();
      const start = performance.now();
      const result = new Promise((resolve, reject) => {
        const timer = setTimeout(() => { sub.dispose(); reject(new Error('native echo timed out')); }, 5000);
        const sub = t.onRender(() => {
          if (!allText(t).includes(marker)) return;
          clearTimeout(timer); sub.dispose(); resolve(performance.now()-start);
        });
      });
      t.input(marker, true);
      const ms = await result;
      t.input('\b'.repeat(marker.length), true);
      await wait(40);
      return ms;
    };
    const latency = [];
    for (let i=0;i<40;i++) latency.push(await echo('perf'+String(i).padStart(2,'0')));
    const dispatch = data => chrome.webview.dispatchEvent(new MessageEvent('message',{data}));
    const synced = setInterval(() => dispatch({type:'pane.out',paneId:firstPane,b64:btoa('\x1b[?2026h\x1b[?2026l')}), 8);
    const synchronizedLatency = [];
    try { for (let i=0;i<40;i++) synchronizedLatency.push(await echo('sync'+String(i).padStart(2,'0'))); }
    finally { clearInterval(synced); }
    await wait(150);
    send({type:'session.new'});
    await wait(1200);
    const secondId = window.__perch.state.activeSessionId;
    if (firstId === secondId) throw new Error('second session did not open');
    const secondPane = window.__perch.state.activePaneId;
    send({type:'session.select',id:firstId}); await wait(200);
    const flood = btoa(('background redraw '+ 'x'.repeat(90) + '\r\n').repeat(70));
    const flooding = setInterval(() => dispatch({type:'pane.out',paneId:secondPane,b64:flood}), 8);
    const loadedLatency = [];
    try { for (let i=0;i<40;i++) loadedLatency.push(await echo('load'+String(i).padStart(2,'0'))); }
    finally { clearInterval(flooding); }
    send({type:'session.select',id:secondId}); await wait(200);
    const baseline = window.__perchTerms.map(t => t._addonManager._addons.length);
    for (let i=0;i<40;i++) { send({type:'session.select',id:i%2?secondId:firstId}); await wait(90); }
    await wait(200);
    const afterSwitch = window.__perchTerms.map(t => t._addonManager._addons.length);
    send({type:'session.select',id:firstId}); await wait(200);
    // A deterministic history fixture exercises the real Pane consumer and
    // xterm, but never executes shell commands or consumes agent tokens.
    dispatch({type:'pane.out',paneId:firstPane,b64:btoa('\r\n'+Array.from({length:1500},(_,i)=>'saved-history-'+i+'\r\n').join(''))});
    await wait(400);
    const before = allText(term());
    const historyPresent = before.includes('saved-history-0') && before.includes('saved-history-1499');
    dispatch({type:'pane.out',paneId:firstPane,b64:btoa('\x1b[?1049h\x1b[HALTERNATE-SCREEN-SAVED')});
    await wait(150);
    send({type:'session.dormant',id:firstId});
    await wait(1200);
    const sleepingTerms = window.__perchTerms.length;
    send({type:'session.select',id:firstId});
    await wait(1200);
    const restored = allText(term());
    const historyRestored = restored.includes('saved-history-0') && restored.includes('saved-history-1499');
    const alternateRestored = restored.includes('ALTERNATE-SCREEN-SAVED');
    const wakeEcho = await echo('wakeproof');
    document.documentElement.style.background = '#1f1f1f';
    const rect = term().element.getBoundingClientRect();
    return {latencyMs:latency,synchronizedLatencyMs:synchronizedLatency,loadedLatencyMs:loadedLatency,wakeEchoMs:wakeEcho,baselineAddons:baseline,afterSwitchAddons:afterSwitch,
      sleepingTerms,awakeTerms:window.__perchTerms.length,historyPresent,historyRestored,alternateRestored,
      geometry:{width:rect.width,height:rect.height}, heapBytes:performance.memory?.usedJSHeapSize};
  })()`);
  writeFileSync(join(output, 'measurements.json'), JSON.stringify(result, null, 2));
  const screenshot = await call('Page.captureScreenshot', { format: 'png' });
  writeFileSync(join(output, 'sleep-wake.png'), Buffer.from(screenshot.data, 'base64'));
  if (!baseline) {
  assert.deepEqual(result.afterSwitchAddons.slice().sort(), result.baselineAddons.slice().sort());
  assert.equal(result.sleepingTerms, 1);
  assert.equal(result.awakeTerms, 2);
  assert.ok(result.historyPresent && result.historyRestored && result.alternateRestored);
  assert.ok(result.geometry.width > 0 && result.geometry.height > 0);
  }
  console.log(JSON.stringify(result));
} finally { ws.close(); }
