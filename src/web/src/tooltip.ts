// Lightweight custom tooltip. One shared floating element, shown on hover
// after a short delay and positioned under (or above, if it would clip) the
// anchor. Used for the pane name, where we want to surface the full original
// prompt that the 40-char label was cut from — a native `title` attribute
// can't be styled to match the Fluent chrome and truncates oddly on long
// multi-line text.
//
// attachTooltip(el, getText) reads the text lazily on each hover via getText()
// so callers can keep returning the latest value (the pane name updates as the
// agent re-titles). Returning "" suppresses the tooltip for that hover.

const SHOW_DELAY_MS = 450;

let tip: HTMLElement | null = null;
let showTimer = 0;
let currentAnchor: HTMLElement | null = null;

function ensureTip(): HTMLElement {
  if (tip) return tip;
  const el = document.createElement("div");
  el.className = "cmux-tooltip";
  el.setAttribute("role", "tooltip");
  document.body.appendChild(el);
  tip = el;
  return el;
}

function hide() {
  if (showTimer) { clearTimeout(showTimer); showTimer = 0; }
  currentAnchor = null;
  if (tip) tip.classList.remove("cmux-tooltip--visible");
}

function showFor(anchor: HTMLElement, text: string) {
  const el = ensureTip();
  el.textContent = text;
  // Measure off-screen first, then place. Anchor below the element, left-
  // aligned; flip above if it would run past the viewport bottom, and clamp
  // horizontally so a long tooltip never spills off the right edge.
  el.classList.add("cmux-tooltip--visible");
  const a = anchor.getBoundingClientRect();
  const t = el.getBoundingClientRect();
  const margin = 8;
  let left = a.left;
  if (left + t.width > window.innerWidth - margin) {
    left = Math.max(margin, window.innerWidth - t.width - margin);
  }
  let top = a.bottom + 6;
  if (top + t.height > window.innerHeight - margin) {
    top = Math.max(margin, a.top - t.height - 6);
  }
  el.style.left = `${left}px`;
  el.style.top = `${top}px`;
}

export function attachTooltip(anchor: HTMLElement, getText: () => string) {
  anchor.addEventListener("mouseenter", () => {
    const text = getText().trim();
    if (!text) return;
    currentAnchor = anchor;
    if (showTimer) clearTimeout(showTimer);
    showTimer = window.setTimeout(() => {
      showTimer = 0;
      if (currentAnchor === anchor) showFor(anchor, text);
    }, SHOW_DELAY_MS);
  });
  anchor.addEventListener("mouseleave", hide);
  // Any mousedown (e.g. starting a rename / clicking the pane) dismisses it
  // immediately so it doesn't hang over a click target.
  anchor.addEventListener("mousedown", hide);
}
