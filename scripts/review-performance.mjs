// Read-only mechanism probes; no app launch, agents, profiles, or user state.
// Run from repository root: node scripts/review-performance.mjs
import { transform } from '../src/web/node_modules/esbuild/lib/main.js';
import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';

async function sourceModule(path) {
  const { code } = await transform(readFileSync(path, 'utf8'), { loader: 'ts', format: 'esm' });
  return import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
}

const { AddonManager } = await sourceModule('src/web/node_modules/@xterm/xterm/src/common/public/AddonManager.ts');
for (const throws of [false, true]) {
  const manager = new AddonManager();
  for (let i = 0; i < 100; i++) {
    // Model the disposal exception documented in Pane.unloadWebgl and present
    // in the installed WebglAddon. This is not a real GPU allocation probe.
    const addon = { activate() {}, dispose() { if (throws) throw new TypeError('_core._store undefined'); } };
    manager.loadAddon({}, addon);
    try { addon.dispose(); } catch { /* same outer catch as Pane */ }
  }
  const retained = manager._addons.length;
  assert.equal(retained, throws ? 100 : 0);
  console.log(JSON.stringify({ probe: 'addon-manager', throws, cycles: 100, retained }));
}

const { SyncBatcher } = await sourceModule('src/web/src/sync-output.ts');
const encoder = new TextEncoder();
let immediate = false;
const shell = new SyncBatcher(() => { immediate = true; });
shell.feed(encoder.encode('echo'));
assert.equal(immediate, true);
shell.dispose();
console.log(JSON.stringify({ probe: 'plain-shell', synchronous: immediate }));

let finish;
const drained = new Promise(resolve => { finish = resolve; });
const start = performance.now();
const batcher = new SyncBatcher(() => finish(performance.now() - start));
batcher.feed(encoder.encode('\x1b[?2026hframe\x1b[?2026l'));
const interval = setInterval(() => batcher.feed(encoder.encode('echo')), 10);
try {
  console.log(JSON.stringify({ probe: 'continuous-sync-output', holdMs: await drained }));
} finally {
  clearInterval(interval);
  batcher.dispose();
}
