// Sidebar = the session list + the New Session button. Hand-rolled because
// the chrome is small and we want the DOM to mirror the host state shape
// exactly. Reconciles diff-free: clear + rebuild on every state push.
//
// The list is split into sections driven purely by derived agent state:
//   - "Needs you" : sessions blocked on a permission prompt (or a genuine,
//                   reserved "waiting"). The agent CAN'T proceed without you.
//                   Each row also shows the agent's note (its ask).
//   - "Idle"      : sessions whose agent finished its turn ("done") and is at
//                   rest — your move, but nothing is blocked and there's no
//                   rush. Calm, single-line rows, no alarming note.
//   - "Projects"  : everything else (working / dormant idle), single-line rows.
// Each row is a framed card (see .session-item in style.css) — perch-style
// tabs-as-cards. Selection still reads via fill, not an accent stripe.

import type { SessionView } from "./bridge.js";
import { send } from "./bridge.js";
import { confirmDialog } from "./confirm.js";

/** "+142 −38" diff summary (U+2212 minus). Empty when nothing changed, so the
 *  caller can omit the item entirely. Either clause is dropped when zero. */
export function fmtDiff(added: number, deleted: number): string {
  if (!added && !deleted) return "";
  const parts: string[] = [];
  if (added) parts.push(`+${added}`);
  if (deleted) parts.push(`−${deleted}`);
  return parts.join(" ");
}

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
    // Partition by derived state. permission (+ the reserved "waiting") want
    // your attention (Needs you); done is "finished, at rest, your move" (Idle);
    // working/dormant-idle are just the map (Projects). Order within each
    // section follows the host's session order (stable) — no resort, so rows
    // don't jump.
    const needs = sessions.filter(
      (s) => s.agentState === "waiting" || s.agentState === "permission"
    );
    const idle = sessions.filter((s) => s.agentState === "done");
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

    if (idle.length) {
      frag.appendChild(this.sectionLabel("Idle", idle.length));
      const list = document.createElement("div");
      list.className = "session-list";
      for (const s of idle)
        list.appendChild(this.renderItem(s, s.id === activeId, false));
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

    // Secondary line(s): one state-aware signal, then pane breakdown. flex-wrap
    // lets a dense session spill onto a second muted line.
    //   working → "▸ what it's doing"
    //   done    → "+A −D · ⎇ branch ↑N"  (what it produced / what's unpushed)
    //   else    → "⎇ branch · :ports"     (dormant / needs-you keep code context)
    const metaItems: Array<{ text: string; alert?: boolean }> = [];
    const ahead = s.ahead > 0 ? ` ↑${s.ahead}` : "";

    if (s.agentState === "working") {
      metaItems.push({ text: `▸ ${s.activityDetail || "working"}` });
    } else if (s.agentState === "done") {
      const diff = fmtDiff(s.linesAdded, s.linesDeleted);
      if (diff) metaItems.push({ text: diff });
      if (s.branch) metaItems.push({ text: `⎇ ${s.branch}${ahead}` });
    } else {
      // dormant idle / needs-you: branch (+ unpushed) + dev-server ports.
      if (s.branch) metaItems.push({ text: `⎇ ${s.branch}${ahead}` });
      for (const p of s.ports ?? []) metaItems.push({ text: `:${p}` });
    }

    // Pane breakdown, appended for any multi-pane session.
    if (s.paneCount > 1) {
      const parts: string[] = [`${s.paneCount} panes`];
      if (s.waitingCount > 0) parts.push(`${s.waitingCount} waiting`);
      else if (s.workingCount > 0) parts.push(`${s.workingCount} working`);
      metaItems.push({ text: parts.join(" · "), alert: s.waitingCount > 0 });
    }

    if (metaItems.length) {
      const meta = document.createElement("span");
      meta.className = "session-item__meta";
      for (const mi of metaItems) {
        const span = document.createElement("span");
        span.className =
          "session-item__meta-item" + (mi.alert ? " session-item__meta-item--alert" : "");
        span.textContent = mi.text;
        meta.appendChild(span);
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
    // Closing a session tears down its entire pane layout — unrecoverable, and
    // losing a hand-arranged setup stings. Gate it behind a confirm.
    close.addEventListener("click", async (ev) => {
      ev.stopPropagation();
      const panes = s.paneCount === 1 ? "1 pane" : `${s.paneCount} panes`;
      const ok = await confirmDialog({
        title: `Close ${s.title}?`,
        body: `This closes the session and its ${panes}. The pane layout can't be recovered.`,
        confirmLabel: "Close session",
        cancelLabel: "Keep open",
        danger: true,
      });
      if (ok) send({ type: "session.close", id: s.id });
    });
    item.appendChild(close);

    item.addEventListener("click", () => {
      if (!active) send({ type: "session.select", id: s.id });
    });

    return item;
  }
}
