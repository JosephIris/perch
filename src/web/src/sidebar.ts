// Sidebar = the session list + the New Session button. Hand-rolled because
// the chrome is small and we want the DOM to mirror the host state shape
// exactly. Reconciles diff-free: clear + rebuild on every state push.

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

    const headRow = document.createElement("span");
    headRow.className = "session-item__head";
    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = s.title;
    headRow.appendChild(title);
    body.appendChild(headRow);

    const meta = document.createElement("span");
    meta.className = "session-item__meta";
    meta.textContent = s.shell;
    body.appendChild(meta);

    // Stage 4: agent state + branch + ports chips. Wraps onto a second row
    // automatically because of flex-wrap; per-chip margin so wrapped lines
    // breathe.
    const hasMeta =
      s.agentState !== "idle" || s.branch || (s.ports && s.ports.length > 0);
    if (hasMeta) {
      const chips = document.createElement("span");
      chips.className = "session-item__chips";

      if (s.agentState !== "idle") {
        const pill = document.createElement("span");
        pill.className = `chip chip--state chip--state-${s.agentState}`;
        pill.textContent = s.agentState;
        chips.appendChild(pill);
      }
      if (s.branch) {
        const b = document.createElement("span");
        b.className = "chip chip--badge";
        b.textContent = `⎇ ${s.branch}`; // git-branch glyph
        chips.appendChild(b);
      }
      for (const p of s.ports ?? []) {
        const b = document.createElement("span");
        b.className = "chip chip--badge";
        b.textContent = `:${p}`;
        chips.appendChild(b);
      }
      body.appendChild(chips);
    }

    // Notification line: colored dot + text. Same lifetime as
    // NotificationText on the host (stays until next notify or pane close).
    if (s.notification) {
      const note = document.createElement("span");
      note.className = `session-item__note session-item__note--${s.notification.level}`;
      const dot = document.createElement("span");
      dot.className = "session-item__note-dot";
      note.appendChild(dot);
      const text = document.createElement("span");
      text.textContent = s.notification.text;
      note.appendChild(text);
      body.appendChild(note);
    }

    item.appendChild(body);

    const close = document.createElement("button");
    close.type = "button";
    close.className = "session-item__close";
    close.title = "Close session";
    close.setAttribute("aria-label", `Close ${s.title}`);
    close.textContent = "✕";
    close.addEventListener("click", (ev) => {
      ev.stopPropagation();
      send({ type: "session.close", id: s.id });
    });
    item.appendChild(close);

    item.addEventListener("click", () => {
      if (!active) send({ type: "session.select", id: s.id });
    });

    return item;
  }
}
