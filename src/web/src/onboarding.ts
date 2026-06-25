// First-launch onboarding lightbox. A centered modal (Constitution allows
// centering for dialogs only) that introduces the handful of pane gestures and
// shortcuts that aren't obvious. Auto-opened once on a fresh install (gated by
// the host's persisted `onboardingSeen` flag) and re-openable anytime from the
// Settings dialog. Dismissing it tells the host to remember it was seen.

import { send } from "./bridge.js";

let overlay: HTMLElement | null = null;

interface Tip {
  /** Short chips shown on the left (kbd keys or a gesture word like "drag"). */
  chips: string[];
  text: string;
}

const TIPS: Tip[] = [
  { chips: ["Ctrl", "Shift", "D"], text: "Split a pane to the right. Ctrl + Shift + S splits it downward." },
  { chips: ["Ctrl", "Shift", "↔"], text: "Move the active pane within its split, or drag a pane's header onto another pane to rearrange." },
  { chips: ["drag"], text: "Resize panes by dragging the divider between them." },
  { chips: ["Shift", "drag"], text: "Copy by selecting text. Hold Shift to select over a full-screen app like Claude Code; right-click copies or pastes." },
  { chips: ["Ctrl", "Shift", "T"], text: "Open a new session. Ctrl + Shift + A is the dashboard; Ctrl + B toggles the sidebar." },
];

/** Show the welcome lightbox. No-op if it's already open. */
export function showOnboarding(): void {
  if (overlay) return;

  const ov = document.createElement("div");
  ov.className = "onboarding-overlay";
  ov.addEventListener("mousedown", (ev) => {
    if (ev.target === ov) dismiss();
  });

  const card = document.createElement("div");
  card.className = "onboarding-card";
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  card.setAttribute("aria-label", "Welcome to Perch");

  const title = document.createElement("h2");
  title.className = "onboarding-card__title";
  title.textContent = "Welcome to Perch";

  const sub = document.createElement("p");
  sub.className = "onboarding-card__sub";
  sub.textContent = "A calm perch over your agents at work.";

  const list = document.createElement("ul");
  list.className = "onboarding-list";
  for (const tip of TIPS) {
    const li = document.createElement("li");
    li.className = "onboarding-item";

    const chips = document.createElement("span");
    chips.className = "onboarding-chips";
    for (const c of tip.chips) {
      const k = document.createElement("kbd");
      k.className = "onboarding-kbd";
      k.textContent = c;
      chips.appendChild(k);
    }

    const text = document.createElement("span");
    text.className = "onboarding-item__text";
    text.textContent = tip.text;

    li.append(chips, text);
    list.appendChild(li);
  }

  const footer = document.createElement("div");
  footer.className = "onboarding-card__footer";

  const note = document.createElement("span");
  note.className = "onboarding-card__note";
  note.textContent = "Reopen anytime from Settings.";

  const cta = document.createElement("button");
  cta.type = "button";
  cta.className = "settings-btn settings-btn--accent";
  cta.textContent = "Get started";
  cta.addEventListener("click", () => dismiss());

  footer.append(note, cta);
  card.append(title, sub, list, footer);
  ov.appendChild(card);
  document.body.appendChild(ov);
  overlay = ov;

  document.addEventListener("keydown", onKeyDown, true);
  requestAnimationFrame(() => cta.focus());
}

function dismiss(): void {
  if (!overlay) return;
  const el = overlay;
  overlay = null;
  document.removeEventListener("keydown", onKeyDown, true);
  // Tell the host the welcome has been seen so it won't auto-open again.
  // Idempotent — harmless when the user reopened it manually from Settings.
  send({ type: "onboarding.seen" });
  el.classList.add("onboarding-overlay--closing");
  el.addEventListener("animationend", () => el.remove(), { once: true });
  window.setTimeout(() => el.remove(), 260); // reduced-motion fallback
}

function onKeyDown(ev: KeyboardEvent): void {
  if (!overlay) return;
  if (ev.key === "Escape" || ev.key === "Enter") {
    ev.preventDefault();
    ev.stopPropagation();
    dismiss();
  }
}
