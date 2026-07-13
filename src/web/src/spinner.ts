// The sidebar's "agent is working" mark: the same braille spinner Claude Code
// animates in the terminal, so the chrome echoes what the pane is literally
// doing. One shared ticker rewrites every [data-spinner] text node in step —
// all spinners share a phase, which reads as one calm system pulse instead of
// several strobes (and costs one interval, not one per row).
//
// This replaced the "▸ editing hook.ts" prose line on projects-mode working
// rows — see design-loop/working-indicator-mockup.html (round 1, option F).

const FRAMES = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
const FRAME_MS = 100;   // cc's own cadence; slower reads as stutter

/** A spinner glyph span. The ticker animates it as long as it's mounted. */
export function spinnerSpan(className = ""): HTMLElement {
  const e = document.createElement("span");
  if (className) e.className = className;
  e.dataset.spinner = "1";
  e.setAttribute("aria-hidden", "true");
  e.textContent = FRAMES[0];
  return e;
}

let started = false;

/** Start the shared frame ticker once. Idempotent. Honors reduced-motion by
 *  leaving every spinner frozen on its first frame — the working STATE still
 *  reads (a braille glyph in the dot slot), only the motion is dropped. */
export function startSpinnerTicker(): void {
  if (started) return;
  started = true;
  if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) return;
  let frame = 0;
  window.setInterval(() => {
    const els = document.querySelectorAll<HTMLElement>("[data-spinner]");
    if (!els.length) return;   // nothing working — skip the frame advance
    frame = (frame + 1) % FRAMES.length;
    const ch = FRAMES[frame];
    els.forEach((el) => {
      if (el.textContent !== ch) el.textContent = ch;
    });
  }, FRAME_MS);
}
