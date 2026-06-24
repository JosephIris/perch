// Sidebar = the session list + the New Session button. Hand-rolled because
// the chrome is small and we want the DOM to mirror the host state shape
// exactly. Reconciles diff-free: clear + rebuild on every state push.
//
// The list is split into two sections driven purely by derived agent state:
//   - "Needs you" : sessions that are waiting (your feedback) or blocked on a
//                   permission. Each row also shows the agent's note (its ask).
//   - "Projects"  : everything else (working / idle), single-line rows.
// Each row is a framed card (see .session-item in style.css) — cmux-style
// tabs-as-cards. Selection still reads via fill, not an accent stripe.

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
    // Partition by derived state. waiting (feedback) + permission both want
    // your attention; working/idle are just the map. Order within each section
    // follows the host's session order (stable) — no resort, so rows don't jump.
    const needs = sessions.filter(
      (s) => s.agentState === "waiting" || s.agentState === "permission"
    );
    const rest = sessions.filter(
      (s) => s.agentState === "working" || s.agentState === "idle"
    );

    const frag = document.createDocumentFragment();

    if (needs.length) {
      frag.appendChild(this.sectionLabel("Needs you", needs.length));
      const list = document.createElement("div");
      list.className = "session-list";
      for (const s of needs)
        list.appendChild(this.renderItem(s, s.id === activeId, true));
      frag.appendChild(list);
    }

    if (rest.length) {
      frag.appendChild(this.sectionLabel("Projects"));
      const list = document.createElement("div");
      list.className = "session-list";
      for (const s of rest)
        list.appendChild(this.renderItem(s, s.id === activeId, false));
      frag.appendChild(list);
    }

    this.listEl.replaceChildren(frag);
  }

  private sectionLabel(text: string, count?: number): HTMLElement {
    const el = document.createElement("div");
    el.className = "sidebar__section-label";
    el.textContent = text;
    if (count != null) {
      const c = document.createElement("span");
      c.className = "sidebar__section-count";
      c.textContent = ` · ${count}`;
      el.appendChild(c);
    }
    return el;
  }

  private renderItem(
    s: SessionView,
    active: boolean,
    showNote: boolean
  ): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "session-item" + (active ? " session-item--active" : "");
    item.dataset.sessionId = s.id;

    // Status dot on the left. Color comes from CSS via [data-state="..."];
    // idle = hollow ring, working/waiting/permission = solid fill.
    const dot = document.createElement("span");
    dot.className = "session-item__dot";
    dot.dataset.state = s.agentState;
    item.appendChild(dot);

    // Primary line: title only. The shell rides in the footer; the row gives
    // the title the whole line for a single clean ellipsis.
    const primary = document.createElement("span");
    primary.className = "session-item__primary";
    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = s.title;
    primary.appendChild(title);
    item.appendChild(primary);

    // Optional secondary line: pane-count breakdown · branch · ports. The code
    // context stays visible in both sections — code tool first, attention on top.
    const hasBreakdown = s.paneCount > 1;
    const hasBranchPorts = s.branch || (s.ports && s.ports.length > 0);
    if (hasBreakdown || hasBranchPorts) {
      const meta = document.createElement("span");
      meta.className = "session-item__meta";

      if (hasBreakdown) {
        const parts: string[] = [`${s.paneCount} panes`];
        if (s.waitingCount > 0) parts.push(`${s.waitingCount} waiting`);
        else if (s.workingCount > 0) parts.push(`${s.workingCount} working`);
        const breakdown = document.createElement("span");
        breakdown.className =
          "session-item__meta-item" +
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

    // Note line — only in the Needs-you section. Shows the agent's ask; falls
    // back to a state phrase when the hook didn't push notify text. 2-line
    // clamp keeps the framed row tight.
    if (showNote) {
      const level =
        s.notification?.level ?? (s.agentState === "permission" ? "error" : "warn");
      const text =
        s.notification?.text ??
        (s.agentState === "permission"
          ? "Needs your permission"
          : "Waiting for your input");
      const note = document.createElement("span");
      note.className = `session-item__note session-item__note--${level}`;
      const txt = document.createElement("span");
      txt.className = "session-item__note-text";
      txt.textContent = text;
      note.appendChild(txt);
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
