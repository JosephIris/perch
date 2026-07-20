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

/** Warmth bucket for a finished turn. A `done` row already means "the agent
 * handed the turn back to you" — every one of them is actionable. What the
 * sidebar was missing is AGE: eleven equally-bright rows say nothing about
 * which one you dropped ten seconds ago and which has been sitting all
 * afternoon. These four buckets drive the age label's treatment (CSS keys off
 * data-warmth); the state dot is deliberately NOT bucketed, so warmth never
 * competes with the state palette. */
export type Warmth = "hot" | "warm" | "cool" | "cold";

/** hot <2m · warm 2–10m · cool 10m–1h · cold >1h. */
export function warmthFor(ms: number): Warmth {
  const m = Math.max(0, ms) / 60000;
  if (m < 2) return "hot";
  if (m < 10) return "warm";
  if (m < 60) return "cool";
  return "cold";
}

/** Age label for a "your turn" row: "12s" / "4m" / "2h" / "3d".
 *
 * Deliberately ONE unit. fmtElapsed's two-part "2h 6m" is right for a turn you
 * are watching run, but this label's whole job at the cold end is to be quiet,
 * and "2h 6m" is the noisiest string on the row precisely where it matters
 * least — nobody triages on the 6. Coarsening past the hour also stops the
 * label growing wide enough to crowd the title. */
export function fmtAge(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h`;
  return `${Math.floor(h / 24)}d`;
}

/** Create the live age label for a finished turn — single-unit text plus the
 * warmth bucket, both kept current by the shared ticker. */
export function ageSpan(doneAtMs: number): HTMLElement {
  const e = document.createElement("span");
  e.dataset.age = String(doneAtMs);
  const d = Date.now() - doneAtMs;
  e.dataset.warmth = warmthFor(d);
  e.textContent = fmtAge(d);
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
      .querySelectorAll<HTMLElement>("[data-turn-start], [data-since], [data-age]")
      .forEach((el) => {
        const coarse = el.dataset.coarse === "1";
        let next: string | null = null;
        let delta = -1;
        const start = Number(el.dataset.turnStart) || 0;
        const age = Number(el.dataset.age) || 0;
        if (start > 0) {
          delta = now - start;
          next = coarse ? fmtElapsedCoarse(delta) : fmtElapsed(delta);
        } else if (age > 0) {
          delta = now - age;
          next = fmtAge(delta);
        } else {
          const since = Number(el.dataset.since) || 0;
          if (since > 0) {
            delta = now - since;
            next = coarse ? fmtAgoCoarse(delta) : fmtAgo(delta);
          }
        }
        // Only touch the DOM when the rendered value actually changes — a
        // coarse (minute) label then writes ~once a minute, not every tick.
        if (next != null && el.textContent !== next) el.textContent = next;
        // Warmth rides the same walk, for spans that opted in. Same
        // write-only-on-change rule: this attribute changes 3 times an hour,
        // not once a second.
        if (delta >= 0 && el.dataset.warmth != null) {
          const w = warmthFor(delta);
          if (el.dataset.warmth !== w) el.dataset.warmth = w;
        }
      });
  }, 1000);
}
