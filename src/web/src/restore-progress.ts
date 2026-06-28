// Restore-progress lightbox. A sleek, "code-like" modal shown while resumed
// Claude panes come back up: one row per pane, a spinner that flips to a check
// as each session re-attaches. Driven entirely by host messages —
//   restore.begin   → open, list the panes (all spinning)
//   restore.progress → flip one row (resuming → ready / error)
//   restore.done    → all handled; auto-dismiss in 3s (or the Dismiss button).
//
// Monospace pane lines + a leading "›" prompt marker give it a terminal feel
// without aping a title-bar; the surface stays in the Fluent token family
// (same overlay/card as the settings + confirm dialogs).

import type { RestorePaneView } from "./bridge.js";

type RowState = "pending" | "resuming" | "ready" | "error";

// Linger after everything's resumed before auto-closing.
const AUTO_DISMISS_MS = 5000;

export class RestoreProgress {
  private overlay: HTMLElement | null = null;
  private rows = new Map<string, HTMLElement>();
  private dismissTimer: number | null = null;
  private done = false;

  /** Open (or replace) the lightbox for the given panes, all starting as
   *  spinning "queued" rows. */
  begin(panes: RestorePaneView[]): void {
    this.teardown();
    if (!panes.length) return;
    this.done = false;

    const overlay = document.createElement("div");
    overlay.className = "settings-overlay restore-overlay";

    const card = document.createElement("div");
    card.className = "settings-card restore-card";
    card.setAttribute("role", "dialog");
    card.setAttribute("aria-modal", "true");
    card.setAttribute("aria-label", "Restoring sessions");

    const head = document.createElement("div");
    head.className = "restore-card__head";
    const title = document.createElement("h2");
    title.className = "restore-card__title";
    title.textContent = panes.length === 1 ? "Resuming session" : "Resuming sessions";
    const sub = document.createElement("span");
    sub.className = "restore-card__sub";
    sub.textContent = `${panes.length} ${panes.length === 1 ? "pane" : "panes"}`;
    head.append(title, sub);
    card.appendChild(head);

    const list = document.createElement("div");
    list.className = "restore-list";
    for (const p of panes) {
      const row = this.makeRow(p);
      this.rows.set(p.paneId, row);
      list.appendChild(row);
    }
    card.appendChild(list);

    // Auto-dismiss progress bar (animates only once done) + Dismiss button.
    const foot = document.createElement("div");
    foot.className = "restore-card__foot";
    const bar = document.createElement("div");
    bar.className = "restore-bar";
    const fill = document.createElement("div");
    fill.className = "restore-bar__fill";
    bar.appendChild(fill);
    const dismiss = document.createElement("button");
    dismiss.type = "button";
    dismiss.className = "settings-btn settings-btn--subtle restore-dismiss";
    dismiss.textContent = "Dismiss";
    dismiss.addEventListener("click", () => this.teardown());
    foot.append(bar, dismiss);
    card.appendChild(foot);

    overlay.appendChild(card);
    document.body.appendChild(overlay);
    this.overlay = overlay;

    // Esc closes anytime; backdrop click only once everything's settled so a
    // stray click mid-resume doesn't kill the view.
    document.addEventListener("keydown", this.onKeyDown, true);
    overlay.addEventListener("mousedown", (ev) => {
      if (ev.target === overlay && this.done) this.teardown();
    });
  }

  /** Flip one pane's row. Unknown pane ids are ignored. */
  progress(paneId: string, state: "resuming" | "ready" | "error"): void {
    const row = this.rows.get(paneId);
    if (row) this.setRowState(row, state);
  }

  /** All panes handled — settle the view and start the 3s auto-dismiss. */
  finish(): void {
    if (!this.overlay) return;
    this.done = true;
    // Any row the host never flipped (shouldn't happen — it force-completes)
    // resolves to ready so nothing is left spinning.
    for (const row of this.rows.values())
      if (row.dataset.state === "pending" || row.dataset.state === "resuming")
        this.setRowState(row, "ready");

    this.overlay.classList.add("restore-overlay--done");
    // Kick the auto-dismiss bar animation, then remove.
    const fill = this.overlay.querySelector<HTMLElement>(".restore-bar__fill");
    if (fill) {
      fill.style.transition = `transform ${AUTO_DISMISS_MS}ms linear`;
      // next frame so the transition actually runs from 0 → full
      requestAnimationFrame(() => {
        fill.style.transform = "scaleX(1)";
      });
    }
    this.dismissTimer = window.setTimeout(() => this.teardown(), AUTO_DISMISS_MS);
  }

  private makeRow(p: RestorePaneView): HTMLElement {
    const row = document.createElement("div");
    row.className = "restore-row";
    row.dataset.state = "pending";

    const status = document.createElement("span");
    status.className = "restore-row__status";
    status.appendChild(spinner());
    row.appendChild(status);

    const text = document.createElement("span");
    text.className = "restore-row__text";
    const marker = document.createElement("span");
    marker.className = "restore-row__marker";
    marker.textContent = "›";
    const name = document.createElement("span");
    name.className = "restore-row__name";
    name.textContent = p.name;
    const sess = document.createElement("span");
    sess.className = "restore-row__sess";
    sess.textContent = p.sessionTitle;
    text.append(marker, name, sess);
    row.appendChild(text);

    const label = document.createElement("span");
    label.className = "restore-row__label";
    label.textContent = "queued";
    row.appendChild(label);

    return row;
  }

  private setRowState(row: HTMLElement, state: RowState): void {
    if (row.dataset.state === state) return;
    row.dataset.state = state;
    const status = row.querySelector<HTMLElement>(".restore-row__status");
    const label = row.querySelector<HTMLElement>(".restore-row__label");
    if (!status || !label) return;
    if (state === "resuming") {
      status.replaceChildren(spinner());
      label.textContent = "resuming…";
    } else if (state === "ready") {
      status.replaceChildren(checkIcon());
      label.textContent = "resumed";
    } else if (state === "error") {
      status.replaceChildren(crossIcon());
      label.textContent = "failed";
    }
  }

  private onKeyDown = (ev: KeyboardEvent) => {
    if (ev.key === "Escape") {
      ev.preventDefault();
      ev.stopPropagation();
      this.teardown();
    }
  };

  private teardown(): void {
    if (this.dismissTimer != null) {
      window.clearTimeout(this.dismissTimer);
      this.dismissTimer = null;
    }
    document.removeEventListener("keydown", this.onKeyDown, true);
    this.rows.clear();
    const ov = this.overlay;
    this.overlay = null;
    this.done = false;
    if (!ov) return;
    ov.classList.add("settings-overlay--closing");
    ov.addEventListener("animationend", () => ov.remove(), { once: true });
    window.setTimeout(() => ov.remove(), 260); // reduced-motion fallback
  }
}

// ---- glyphs ---------------------------------------------------------------

function svg(width = 14): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const el = document.createElementNS(ns, "svg");
  el.setAttribute("width", String(width));
  el.setAttribute("height", String(width));
  el.setAttribute("viewBox", "0 0 24 24");
  el.setAttribute("fill", "none");
  el.setAttribute("stroke", "currentColor");
  el.setAttribute("stroke-width", "2");
  el.setAttribute("stroke-linecap", "round");
  el.setAttribute("stroke-linejoin", "round");
  el.setAttribute("aria-hidden", "true");
  return el;
}

/** Indeterminate spinner — a 3/4 ring; CSS spins it. */
function spinner(): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const el = svg(14);
  el.classList.add("restore-spinner");
  const path = document.createElementNS(ns, "path");
  // 270° arc, leaving a gap so the rotation reads.
  path.setAttribute("d", "M12 3a9 9 0 1 0 9 9");
  el.appendChild(path);
  return el;
}

function checkIcon(): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const el = svg(14);
  el.classList.add("restore-check");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", "M20 6 9 17l-5-5");
  el.appendChild(path);
  return el;
}

function crossIcon(): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const el = svg(14);
  el.classList.add("restore-cross");
  const a = document.createElementNS(ns, "path");
  a.setAttribute("d", "M18 6 6 18");
  const b = document.createElementNS(ns, "path");
  b.setAttribute("d", "M6 6l12 12");
  el.append(a, b);
  return el;
}
