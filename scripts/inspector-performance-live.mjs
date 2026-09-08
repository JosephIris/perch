// Explicit isolated instance only; registers a synthetic journal through its
// existing per-pane IPC. Never starts an agent or reads an owner's transcript.
// Usage: node scripts/inspector-performance-live.mjs PORT SCRATCH_ROOT OUTPUT_DIR
import { execFileSync } from 'node:child_process';
import { createConnection } from 'node:net';
import { writeFileSync, appendFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import assert from 'node:assert/strict';
const [port, scratch, output] = process.argv.slice(2);
if (!port || !scratch || !output) throw new Error('Explicit isolated port, scratch root, and output directory required');
const evaluate = expression => JSON.parse(execFileSync(process.execPath, ['scripts/cdp-eval.mjs', expression, '15000', port], { encoding: 'utf8' }).trim());
const state = evaluate('({pane:window.__perch.state.activePaneId,session:window.__perch.state.activeSessionId})');
const fixture = join(scratch, 'journal-fixture.jsonl');
const row = i => JSON.stringify({ type: 'event_msg', timestamp: '2026-09-08T12:00:00Z', payload: { type: 'turn_aborted', reason: `fixture-${i}` } }) + '\n';
writeFileSync(fixture, Array.from({ length: 1000 }, (_, i) => row(i)).join(''));
evaluate(`(() => { window.__journalWire=[]; chrome.webview.addEventListener('message', e => { const m=typeof e.data==='string'?JSON.parse(e.data):e.data; if(m.type==='inspector.data') window.__journalWire.push({events:m.events.length,start:m.eventStart,bytes:JSON.stringify(m).length}); }); return true; })()`);
await new Promise((resolve, reject) => {
  const pipe = createConnection(`\\\\.\\pipe\\perch\\${state.pane.replaceAll('-', '')}`);
  pipe.on('error', reject);
  pipe.on('connect', () => pipe.end(JSON.stringify({ type: 'session', id: 'performance-fixture', agent: 'codex', path: fixture }) + '\n', resolve));
});
const refresh = async () => evaluate(`(async()=>{chrome.webview.postMessage(JSON.stringify({type:'session.select',id:${JSON.stringify(state.session)}})); await new Promise(r=>setTimeout(r,700)); return document.getElementById('inspector-stream').children.length;})()`);
await refresh();
const initial = evaluate(`(()=>{const host=document.getElementById('inspector-stream');window.__journalFirst=host.firstElementChild;host.scrollTop=100;return {rows:host.children.length,wire:window.__journalWire.at(-1)};})()`);
await refresh();
const unchanged = evaluate(`({sameRow:window.__journalFirst===document.getElementById('inspector-stream').firstElementChild,wire:window.__journalWire.at(-1)})`);
appendFileSync(fixture, row(1000));
await refresh();
const appended = evaluate(`({sameRow:window.__journalFirst===document.getElementById('inspector-stream').firstElementChild,rows:document.getElementById('inspector-stream').children.length,wire:window.__journalWire.at(-1)})`);
const result = { initial, unchanged, appended };
mkdirSync(output, { recursive: true });
writeFileSync(join(output, 'inspector.json'), JSON.stringify(result, null, 2));
console.log(JSON.stringify(result));
if (!process.argv.includes("--baseline")) {
assert.equal(initial.rows, 1000);
assert.equal(unchanged.wire.events, 0);
assert.equal(unchanged.sameRow, true);
assert.equal(appended.rows, 1001);
assert.equal(appended.wire.events, 1);
assert.equal(appended.sameRow, true);

}
