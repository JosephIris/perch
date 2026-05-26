// Tiny toast surface for `cmux notify` events. Same lifetime story as a
// browser toast: show, hold for a few seconds, fade. Multiple notifies
// land in quick succession during agent activity, so we cancel the
// outgoing timer when a new one arrives instead of stacking.

import type { NotificationLevel } from "./bridge.js";

const HOLD_MS = 3200;

export class Toast {
  private readonly el: HTMLElement;
  private readonly dot: HTMLElement;
  private readonly textEl: HTMLElement;
  private timer: number | null = null;

  constructor(el: HTMLElement) {
    this.el = el;
    this.dot = el.querySelector(".toast__dot") as HTMLElement;
    this.textEl = el.querySelector(".toast__text") as HTMLElement;
  }

  show(text: string, level: NotificationLevel) {
    this.textEl.textContent = text;
    // Reset variant classes and apply the new one.
    this.el.classList.remove(
      "toast--info", "toast--success", "toast--warn", "toast--error",
    );
    this.el.classList.add(`toast--${level}`);
    this.el.classList.add("is-open");

    if (this.timer != null) clearTimeout(this.timer);
    this.timer = window.setTimeout(() => {
      this.el.classList.remove("is-open");
      this.timer = null;
    }, HOLD_MS);
  }
}
