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

/** Coarse elapsed: minute granularity only ("<1m" / "2m" / "1h 5m"). Used by
 *  surfaces (the pane footer) that don't want a per-second seconds counter
 *  flickering — the value changes at most once a minute. */
export function fmtElapsedCoarse(ms: number): string {
  const m = Math.max(0, Math.floor(ms / 60000));
  if (m < 1) return "<1m";
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}

/** Coarse "ago": minute granularity ("just now" / "2m ago" / "1h ago"). */
export function fmtAgoCoarse(ms: number): string {
  const m = Math.max(0, Math.floor(ms / 60000));
  if (m < 1) return "just now";
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

/** Create a span that auto-updates to the elapsed since `turnStartMs`.
 *  `coarse` drops the seconds counter (minute granularity) — see the footer. */
export function elapsedSpan(turnStartMs: number, coarse = false): HTMLElement {
  const e = document.createElement("span");
  e.dataset.turnStart = String(turnStartMs);
  if (coarse) e.dataset.coarse = "1";
  const d = Date.now() - turnStartMs;
  e.textContent = coarse ? fmtElapsedCoarse(d) : fmtElapsed(d);
  return e;
}

/** Create a span that auto-updates to the relative-ago since `doneAtMs` (the
 * Unix-ms a turn finished). Same shared ticker as elapsedSpan, so a done row's
 * "finished · 2m ago" stays live without the host re-pushing state. `coarse`
 * drops the sub-minute seconds. */
export function agoSpan(doneAtMs: number, coarse = false): HTMLElement {
  const e = document.createElement("span");
  e.dataset.since = String(doneAtMs);
  if (coarse) e.dataset.coarse = "1";
  const d = Date.now() - doneAtMs;
  e.textContent = coarse ? fmtAgoCoarse(d) : fmtAgo(d);
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
        const coarse = el.dataset.coarse === "1";
        let next: string | null = null;
        const start = Number(el.dataset.turnStart) || 0;
        if (start > 0) {
          next = coarse ? fmtElapsedCoarse(now - start) : fmtElapsed(now - start);
        } else {
          const since = Number(el.dataset.since) || 0;
          if (since > 0) next = coarse ? fmtAgoCoarse(now - since) : fmtAgo(now - since);
        }
        // Only touch the DOM when the rendered value actually changes — a
        // coarse (minute) label then writes ~once a minute, not every tick.
        if (next != null && el.textContent !== next) el.textContent = next;
      });
  }, 1000);
}
