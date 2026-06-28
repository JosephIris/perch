// Live "working · 2m" elapsed labels. The host pushes state only on
// transitions, not every second, so a server-computed elapsed would sit
// stale while a pane works silently. Instead each elapsed label carries the
// turn-start (Unix-ms) in data-turn-start, and one shared 1Hz interval
// rewrites just those text nodes — no sidebar/dashboard rebuild, so it's
// cheap and never fights the component renders.

/** Compact elapsed: "8s" / "2m" / "1h 5m". Clamps negatives to 0. */
export function fmtElapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}

/** Relative "ago" for a finished turn: "now" / "8s ago" / "2m ago" / "1h ago"
 * / "3d ago". Calmer than fmtElapsed (the turn is at rest, not counting up),
 * so it coarsens past the hour to one unit. Clamps negatives to "now". */
export function fmtAgo(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 5) return "now";
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

/** Create a span that auto-updates to the elapsed since `turnStartMs`. */
export function elapsedSpan(turnStartMs: number): HTMLElement {
  const e = document.createElement("span");
  e.dataset.turnStart = String(turnStartMs);
  e.textContent = fmtElapsed(Date.now() - turnStartMs);
  return e;
}

/** Create a span that auto-updates to the relative-ago since `doneAtMs` (the
 * Unix-ms a turn finished). Same shared ticker as elapsedSpan, so a done row's
 * "finished · 2m ago" stays live without the host re-pushing state. */
export function agoSpan(doneAtMs: number): HTMLElement {
  const e = document.createElement("span");
  e.dataset.since = String(doneAtMs);
  e.textContent = fmtAgo(Date.now() - doneAtMs);
  return e;
}

let started = false;

/** Start the shared ticker once. Idempotent. */
export function startElapsedTicker(): void {
  if (started) return;
  started = true;
  window.setInterval(() => {
    const now = Date.now();
    // Two kinds of live time labels share this tick: forward-counting elapsed
    // on working rows (data-turn-start) and relative-ago on finished rows
    // (data-since). One DOM walk, branch per node.
    document
      .querySelectorAll<HTMLElement>("[data-turn-start], [data-since]")
      .forEach((el) => {
        const start = Number(el.dataset.turnStart) || 0;
        if (start > 0) {
          el.textContent = fmtElapsed(now - start);
          return;
        }
        const since = Number(el.dataset.since) || 0;
        if (since > 0) el.textContent = fmtAgo(now - since);
      });
  }, 1000);
}
