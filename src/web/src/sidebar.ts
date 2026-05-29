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

    // Status dot on the left. Color comes from CSS via [data-state="..."];
    // idle = hollow ring, working/waiting/done/error = solid fill. Replaces
    // the old agent-state chip pill — single signal, single position.
    const dot = document.createElement("span");
    dot.className = "session-item__dot";
    dot.dataset.state = s.agentState;
    item.appendChild(dot);

    // Primary line: title only. The shell used to ride here as a "· pwsh"
    // suffix, but in a 240px sidebar it forced BOTH the title and the shell
    // to truncate ("product-tools-... · powers..."), which is hard to parse.
    // The active session's shell already shows in the footer, so the row
    // gives the title the whole line for a single clean ellipsis instead.
    const primary = document.createElement("span");
    primary.className = "session-item__primary";

    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = s.title;
    primary.appendChild(title);
    item.appendChild(primary);

    // Optional secondary line: pane-count breakdown · branch · ports.
    // Pane count is the at-a-glance signal for the user's workflow ("3 panes,
    // 1 waiting on me") — when waitingCount > 0 we tint it caution so it
    // catches the eye even peripherally.
    const hasBreakdown = s.paneCount > 1;   // single-pane session = no breakdown row
    const hasBranchPorts = s.branch || (s.ports && s.ports.length > 0);
    if (hasBreakdown || hasBranchPorts) {
      const meta = document.createElement("span");
      meta.className = "session-item__meta";

      if (hasBreakdown) {
        const parts: string[] = [`${s.paneCount} panes`];
        if (s.waitingCount > 0) parts.push(`${s.waitingCount} waiting`);
        else if (s.workingCount > 0) parts.push(`${s.workingCount} working`);
        const breakdown = document.createElement("span");
        breakdown.className = "session-item__meta-item" +
          (s.waitingCount > 0 ? " session-item__meta-item--alert" : "");
        breakdown.textContent = parts.join(" · ");
        meta.appendChild(breakdown);
      }

      if (s.branch) {
        const b = document.createElement("span");
        b.className = "session-item__meta-item";
        b.textContent = `⎇ ${s.branch}`;
        meta.appendChild(b);
      }
      for (const p of s.ports ?? []) {
        const b = document.createElement("span");
        b.className = "session-item__meta-item";
        b.textContent = `:${p}`;
        meta.appendChild(b);
      }
      item.appendChild(meta);
    }

    if (s.notification) {
      const note = document.createElement("span");
      note.className = `session-item__note session-item__note--${s.notification.level}`;
      note.textContent = s.notification.text;
      item.appendChild(note);
    }

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
