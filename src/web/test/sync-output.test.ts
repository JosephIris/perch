// The frame batcher. The timings here are the ones measured from codex 0.153
// through ConPTY (see sync-output.ts): a frame's second burst lands 8–11 ms
// after the closing mark, and frames are at least 24 ms apart.

import { test, mock } from "node:test";
import assert from "node:assert/strict";
import { SyncBatcher, hasSyncBegin } from "../src/sync-output.js";

const enc = new TextEncoder();
const b = (s: string) => enc.encode(s);
const dec = new TextDecoder();
const BEGIN = "\x1b[?2026h";
const END = "\x1b[?2026l";

function batcher(clock: { t: number }, quietMs = 16, maxMs = 100) {
  const writes: string[] = [];
  const sb = new SyncBatcher((bytes) => writes.push(dec.decode(bytes)), { quietMs, maxMs, now: () => clock.t });
  return { sb, writes };
}

test("hasSyncBegin finds the mark anywhere, and only the begin mark", () => {
  assert.equal(hasSyncBegin(b(BEGIN)), true);
  assert.equal(hasSyncBegin(b("abc" + BEGIN + "def")), true);
  assert.equal(hasSyncBegin(b(END)), false);
  assert.equal(hasSyncBegin(b("\x1b[?25l\x1b[24;1H\x1b[?25h")), false);
  assert.equal(hasSyncBegin(b("")), false);
  assert.equal(hasSyncBegin(b("\x1b[?2026")), false);
});

test("bytes with no sync mark pass straight through, synchronously", () => {
  const clock = { t: 0 };
  const { sb, writes } = batcher(clock);
  sb.feed(b("$ ls\r\n"));
  sb.feed(b("a.txt\r\n"));
  assert.deepEqual(writes, ["$ ls\r\n", "a.txt\r\n"]);
  assert.equal(sb.holding, false);
});

test("a codex frame — begin, anchor hop, end, trailing return — reaches xterm as ONE write", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const clock = { t: 1000 };
    const { sb, writes } = batcher(clock);
    sb.feed(b(BEGIN));
    sb.feed(b("\x1b[?25l\x1b[21;1H\x1b[?25h"));
    sb.feed(b("\x1b[0 q"));
    sb.feed(b(END));
    assert.deepEqual(writes, []);                       // nothing drawn mid-frame
    clock.t += 8; mock.timers.tick(8);                  // the trailing burst, 8 ms later
    sb.feed(b("\x1b[?25l \x1b[30;3H\x1b[?25h"));
    assert.deepEqual(writes, []);
    clock.t += 16; mock.timers.tick(16);                // quiet → flush
    assert.equal(writes.length, 1);
    assert.equal(writes[0], BEGIN + "\x1b[?25l\x1b[21;1H\x1b[?25h" + "\x1b[0 q" + END + "\x1b[?25l \x1b[30;3H\x1b[?25h");
    assert.equal(sb.holding, false);
  } finally { mock.timers.reset(); }
});

test("the next frame, 24 ms on, is its own write", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const clock = { t: 0 };
    const { sb, writes } = batcher(clock);
    sb.feed(b(BEGIN + "one" + END));
    clock.t += 16; mock.timers.tick(16);
    clock.t += 8; mock.timers.tick(8);
    sb.feed(b(BEGIN + "two" + END));
    clock.t += 16; mock.timers.tick(16);
    assert.deepEqual(writes, [BEGIN + "one" + END, BEGIN + "two" + END]);
  } finally { mock.timers.reset(); }
});

test("under continuous output a batch is released by the max hold, not starved", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const clock = { t: 0 };
    const { sb, writes } = batcher(clock);
    sb.feed(b(BEGIN));
    for (let i = 0; i < 30; i++) {            // a chunk every 5 ms for 150 ms
      clock.t += 5; mock.timers.tick(5);
      sb.feed(b("x"));
    }
    assert.ok(writes.length >= 1, "something was written before 150 ms");
    assert.equal(writes[0].startsWith(BEGIN), true);
    // What came after the forced flush carried no begin mark, so it went
    // straight through — no hold outlives its frame.
    const rest = writes.slice(1).join("");
    assert.equal(rest.includes(BEGIN), false);
  } finally { mock.timers.reset(); }
});

test("flush and dispose", () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const clock = { t: 0 };
    const { sb, writes } = batcher(clock);
    sb.feed(b(BEGIN + "held"));
    sb.flush();
    assert.deepEqual(writes, [BEGIN + "held"]);
    sb.feed(b(BEGIN + "dropped"));
    sb.dispose();
    clock.t += 50; mock.timers.tick(50);
    assert.deepEqual(writes, [BEGIN + "held"]);
  } finally { mock.timers.reset(); }
});
