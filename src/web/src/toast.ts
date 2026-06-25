// Tiny toast surface for `perch notify` events. Same lifetime story as a
// browser toast: show, hold for a few seconds, fade. Multiple notifies
// land in quick succession during agent activity, so we cancel the
// outgoing timer when a new one arrives instead of stacking.

import type { NotificationLevel } from "./bridge.js";

const HOLD_MS = 3200;

export class Toast {
  private readonly el: HTMLElement;
  private readonly textEl: HTMLElement;
  private readonly home: HTMLElement | null;
  private timer: number | null = null;

  constructor(el: HTMLElement) {
    this.el = el;
    this.textEl = el.querySelector(".toast__text") as HTMLElement;
    // Default parent (window-centered). We re-home the element here when a
    // toast targets no specific pane.
    this.home = el.parentElement;
  }

  /** Show a toast. When `target` is a pane element the toast is re-parented
   *  into it and pinned to that pane's bottom-center; otherwise it sits at
   *  the window's bottom-center (the .pane is position:relative, so absolute
   *  positioning anchors to it). */
  show(text: string, level: NotificationLevel, target?: HTMLElement | null) {
    this.textEl.textContent = text;
    // Reset variant classes and apply the new one.
    this.el.classList.remove(
      "toast--info", "toast--success", "toast--warn", "toast--error",
    );
    this.el.classList.add(`toast--${level}`);
    // Re-home: pin into the firing pane, or fall back to the window.
    if (target) {
      if (this.el.parentElement !== target) target.appendChild(this.el);
      this.el.classList.add("toast--pinned");
    } else {
      if (this.home && this.el.parentElement !== this.home) {
        this.home.appendChild(this.el);
      }
      this.el.classList.remove("toast--pinned");
    }
    this.el.classList.add("is-open");

    if (this.timer != null) clearTimeout(this.timer);
    this.timer = window.setTimeout(() => {
      this.el.classList.remove("is-open");
      this.timer = null;
    }, HOLD_MS);
  }
}
