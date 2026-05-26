// Sidebar = the session list + the New Session button. Hand-rolled because
// the chrome is small enough that a framework would weigh more than it
// saves and we want the DOM to mirror the host state shape exactly.
//
// Reconciles diff-free: on every state push from the host we clear and
// rebuild. The session list is small (<100 rows in any realistic case)
// and clearing + rebuilding is simpler than tracking item identity.

import type { SessionView } from "./bridge.js";
import { send } from "./bridge.js";

export class Sidebar {
  private readonly listEl: HTMLElement;
  private readonly newSessionBtn: HTMLElement;

  constructor(listEl: HTMLElement, newSessionBtn: HTMLElement) {
    this.listEl = listEl;
    this.newSessionBtn = newSessionBtn;

    this.newSessionBtn.addEventListener("click", () => {
      send({ type: "session.new" });
    });
  }

  render(sessions: SessionView[], activeId: string) {
    const items = sessions.map((s) => this.renderItem(s, s.id === activeId));
    this.listEl.replaceChildren(...items);
  }

  private renderItem(s: SessionView, active: boolean): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "session-item" + (active ? " session-item--active" : "");
    item.dataset.sessionId = s.id;

    const accent = document.createElement("span");
    accent.className = "session-item__accent";
    item.appendChild(accent);

    const body = document.createElement("span");
    body.className = "session-item__body";

    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = s.title;
    body.appendChild(title);

    const meta = document.createElement("span");
    meta.className = "session-item__meta";
    meta.textContent = s.shell;
    body.appendChild(meta);

    item.appendChild(body);

    const close = document.createElement("button");
    close.type = "button";
    close.className = "session-item__close";
    close.title = "Close session";
    close.setAttribute("aria-label", `Close ${s.title}`);
    close.textContent = "✕"; // small X
    close.addEventListener("click", (ev) => {
      ev.stopPropagation();           // don't trigger session.select
      send({ type: "session.close", id: s.id });
    });
    item.appendChild(close);

    item.addEventListener("click", () => {
      if (!active) send({ type: "session.select", id: s.id });
    });

    return item;
  }
}
