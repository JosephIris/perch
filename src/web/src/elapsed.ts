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

/** Create a span that auto-updates to the elapsed since `turnStartMs`. */
export function elapsedSpan(turnStartMs: number): HTMLElement {
  const e = document.createElement("span");
  e.dataset.turnStart = String(turnStartMs);
  e.textContent = fmtElapsed(Date.now() - turnStartMs);
  return e;
}

let started = false;

/** Start the shared ticker once. Idempotent. */
export function startElapsedTicker(): void {
  if (started) return;
  started = true;
  window.setInterval(() => {
    const now = Date.now();
    document
      .querySelectorAll<HTMLElement>("[data-turn-start]")
      .forEach((el) => {
        const t = Number(el.dataset.turnStart) || 0;
        if (t > 0) el.textContent = fmtElapsed(now - t);
      });
  }, 1000);
}
