import { test, mock } from "node:test";
import assert from "node:assert/strict";
import { InputPump } from "../src/input-pump.js";
import { SyncBatcher } from "../src/sync-output.js";
import { mergeInspector } from "../src/inspector-delta.js";
import type { InspectorDataMessage } from "../src/bridge.js";

test("large paste waits for native consumption and preserves UTF-8 bytes and subsequent typing", () => {
  const sent: { bytes: Uint8Array; seq: number }[] = [];
  const pump = new InputPump((bytes, seq) => sent.push({ bytes, seq }));
  const paste = new TextEncoder().encode("שלום🙂".repeat(10000));
  pump.enqueue(paste);
  pump.enqueue(new Uint8Array([13]));
  assert.equal(sent.length, 1);
  pump.ack(999); // stale acknowledgements cannot release flow control
  assert.equal(sent.length, 1);
  for (let i = 0; i < sent.length; i++) pump.ack(sent[i].seq);
  assert.ok(sent.every(s => s.bytes.length <= 16384));
  assert.deepEqual(Buffer.concat(sent.map(s => Buffer.from(s.bytes))), Buffer.concat([Buffer.from(paste), Buffer.from([13])]));
});

test("failed or disposed input never sends a paste suffix as a new command", () => {
  let sends = 0;
  const pump = new InputPump(() => sends++);
  pump.enqueue(new Uint8Array(100000));
  pump.ack(1, true);
  assert.equal(sends, 1);
  pump.enqueue(new Uint8Array([1]));
  pump.dispose();
  pump.ack(2);
  assert.equal(sends, 2);
});

test("cold input waits for PTY readiness", () => {
  let sent = 0;
  const pump = new InputPump(() => sent++);
  pump.pause();
  pump.enqueue(new Uint8Array([1]));
  assert.equal(sent, 0);
  pump.resume();
  assert.equal(sent, 1);
});

test("sync deadline cannot be postponed by output and input preserves trailing cursor burst", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const writes: Uint8Array[] = [];
    let now = 0;
    const batcher = new SyncBatcher(b => writes.push(b), { now: () => now });
    batcher.noteInput();
    batcher.feed(new TextEncoder().encode("\x1b[?2026hframe\x1b[?2026l"));
    for (let i = 0; i < 4; i++) { now += 5; mock.timers.tick(5); batcher.feed(new Uint8Array([120])); }
    assert.equal(writes.length, 0);
    now += 4; mock.timers.tick(4);
    assert.equal(writes.length, 1);
    batcher.dispose();
  } finally { mock.timers.reset(); }
});

test("journal revisions reject a missing base and reuse unchanged history", () => {
  const base: InspectorDataMessage = { type: "inspector.data", paneId: "p", hasAgent: true, revision: "a", events: [], vitals: null, files: [], added: 0, deleted: 0 };
  const next = { ...base, revision: "b", baseRevision: "a", eventStart: 0 };
  assert.equal(mergeInspector(undefined, next), null);
  assert.equal(mergeInspector({ ...base, revision: "wrong" }, next), null);
  assert.equal(mergeInspector(base, next)?.events, base.events);
  assert.equal(mergeInspector(base, { ...next, eventStart: 2 }), null);
});
