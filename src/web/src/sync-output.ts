// Synchronized-output batching for a pane's byte stream.
//
// A frame-oriented TUI (codex, and anything on ratatui) paints each frame
// inside DEC private mode 2026 — "synchronized output": `CSI ? 2026 h` …
// frame … `CSI ? 2026 l` — so a terminal that honours it shows only finished
// frames. Windows Terminal does; xterm.js does not, and ConPTY hands the frame
// over in several writes anyway, with one of them landing AFTER the closing
// sequence. Measured against codex 0.153 (scripts in the session notes): every
// frame arrives as a burst inside the sync marks that parks the cursor on a
// top-of-screen "anchor" cell (codex's DECSCUSR workaround for JetBrains
// terminals), then, 8–11 ms later, a second burst that moves it back to the
// composer. xterm renders in between, so the cursor visibly hops between the
// two spots at frame rate — the "blinking like crazy" that survived the
// hide/show smoothing in pane.ts, which never touched cursor POSITION.
//
// The batcher makes a frame atomic on our side: from the first `?2026h`, bytes
// are held until the stream has been quiet for QUIET_MS (longer than the
// intra-frame gap, shorter than the space between frames, which is ≥ 24 ms
// even while codex works), or until MAX_MS have passed under continuous
// output, then handed to xterm as ONE write. xterm parses a write before it
// renders, so no half-frame is ever drawn. Bytes that arrive with no sync
// mark in sight pass straight through, untouched and undelayed — a shell's
// keystroke echo never waits on this.
//
// Kept free of DOM and xterm so the timing rules are unit-testable in node.

/** `CSI ? 2026 h` — begin synchronized update. */
const SYNC_BEGIN = [0x1b, 0x5b, 0x3f, 0x32, 0x30, 0x32, 0x36, 0x68];

/** Whether `bytes` contain a synchronized-update BEGIN mark. */
export function hasSyncBegin(bytes: Uint8Array): boolean {
  const last = bytes.length - SYNC_BEGIN.length;
  outer: for (let i = 0; i <= last; i++) {
    if (bytes[i] !== 0x1b) continue;
    for (let k = 1; k < SYNC_BEGIN.length; k++)
      if (bytes[i + k] !== SYNC_BEGIN[k]) continue outer;
    return true;
  }
  return false;
}

function concat(parts: Uint8Array[]): Uint8Array {
  if (parts.length === 1) return parts[0];
  let n = 0;
  for (const p of parts) n += p.length;
  const all = new Uint8Array(n);
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.length; }
  return all;
}

export interface SyncBatcherOptions {
  /** Quiet time that ends a batch (ms). Default 16 — one display frame. */
  quietMs?: number;
  /** Longest a batch may be held under continuous output (ms). Default 100. */
  maxMs?: number;
  /** Clock, for tests. */
  now?: () => number;
}

export class SyncBatcher {
  private parts: Uint8Array[] = [];
  private held = false;              // a frame is being held
  private since = 0;                 // clock reading when the hold began
  private quietTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly quietMs: number;
  private readonly maxMs: number;
  private readonly now: () => number;

  /** `out` receives each write exactly as xterm should see it. */
  constructor(private readonly out: (bytes: Uint8Array) => void, opts: SyncBatcherOptions = {}) {
    this.quietMs = opts.quietMs ?? 16;
    this.maxMs = opts.maxMs ?? 100;
    this.now = opts.now ?? (() => Date.now());
  }

  /** True while a frame is being held. */
  get holding(): boolean { return this.held; }

  feed(bytes: Uint8Array): void {
    if (!this.held && !hasSyncBegin(bytes)) { this.out(bytes); return; }
    const t = this.now();
    if (!this.held) { this.held = true; this.since = t; }
    this.parts.push(bytes);
    if (this.quietTimer) clearTimeout(this.quietTimer);
    this.quietTimer = null;
    if (t - this.since >= this.maxMs) { this.flush(); return; }
    this.quietTimer = setTimeout(() => this.flush(), this.quietMs);
  }

  /** Hand whatever is held to `out` now, as one write. */
  flush(): void {
    if (this.quietTimer) { clearTimeout(this.quietTimer); this.quietTimer = null; }
    const parts = this.parts;
    this.parts = [];
    this.held = false;
    if (parts.length > 0) this.out(concat(parts));
  }

  dispose(): void {
    if (this.quietTimer) { clearTimeout(this.quietTimer); this.quietTimer = null; }
    this.parts = [];
    this.held = false;
  }
}
