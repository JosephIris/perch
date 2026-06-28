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

import type { SessionView, ClosedSessionView } from "./bridge.js";
import { send } from "./bridge.js";
import { confirmDialog } from "./confirm.js";
import { elapsedSpan, agoSpan } from "./elapsed.js";

export class Sidebar {
  private readonly listEl: HTMLElement;
  private readonly newSessionBtn: HTMLElement;
  private readonly closedEl: HTMLElement;

  constructor(listEl: HTMLElement, newSessionBtn: HTMLElement, closedEl: HTMLElement) {
    this.listEl = listEl;
    this.newSessionBtn = newSessionBtn;
    this.closedEl = closedEl;

    this.newSessionBtn.addEventListener("click", () => {
      send({ type: "session.new" });
    });
  }

  render(sessions: SessionView[], activeId: string, closed: ClosedSessionView[] = []) {
    this.renderClosed(closed);
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

  // "Recently closed" list, pinned above the identity footer. Each row
  // restores the whole session (layout + cwd + Claude resume) on click,
  // behind a confirm; a hover-revealed ✕ discards it from the list. Hidden
  // entirely when nothing's been closed.
  private renderClosed(closed: ClosedSessionView[]) {
    if (!closed.length) {
      this.closedEl.hidden = true;
      this.closedEl.replaceChildren();
      return;
    }
    this.closedEl.hidden = false;

    const frag = document.createDocumentFragment();

    const header = document.createElement("div");
    header.className = "recently-closed__header";
    const label = document.createElement("span");
    label.className = "recently-closed__label";
    label.textContent = "Recently closed";
    const count = document.createElement("span");
    count.className = "recently-closed__count";
    count.textContent = String(closed.length);
    header.append(label, count);
    frag.appendChild(header);

    const list = document.createElement("div");
    list.className = "recently-closed__list";
    for (const c of closed) list.appendChild(this.renderClosedRow(c));
    frag.appendChild(list);

    this.closedEl.replaceChildren(frag);
  }

  private renderClosedRow(c: ClosedSessionView): HTMLElement {
    const panes = c.paneCount === 1 ? "1 pane" : `${c.paneCount} panes`;

    const row = document.createElement("button");
    row.type = "button";
    row.className = "closed-item";
    row.dataset.sessionId = c.id;
    row.title = `Restore ${c.title}`;

    const icon = restoreIcon();
    icon.classList.add("closed-item__icon");
    row.appendChild(icon);

    const text = document.createElement("span");
    text.className = "closed-item__text";

    const title = document.createElement("span");
    title.className = "closed-item__title";
    title.textContent = c.title;
    text.appendChild(title);

    const meta = document.createElement("span");
    meta.className = "closed-item__meta";
    meta.append(panes);
    if (c.resumableCount > 0) {
      const agents = c.resumableCount === 1 ? "1 agent" : `${c.resumableCount} agents`;
      meta.append(` · ${agents}`);
    }
    if (c.closedAtMs > 0) {
      meta.append(" · ");
      meta.appendChild(agoSpan(c.closedAtMs));
    }
    text.appendChild(meta);
    row.appendChild(text);

    // Discard from the list (no restore). Stops the row's restore handler.
    const purge = document.createElement("button");
    purge.type = "button";
    purge.className = "closed-item__purge";
    purge.title = "Remove from recently closed";
    purge.setAttribute("aria-label", `Remove ${c.title} from recently closed`);
    purge.textContent = "✕";
    purge.addEventListener("click", (ev) => {
      ev.stopPropagation();
      send({ type: "session.purge", id: c.id });
    });
    row.appendChild(purge);

    // Restoring relaunches agents, so gate it behind a confirm (the user
    // asked for "are you sure?" before bringing a closed project back).
    row.addEventListener("click", async () => {
      const agents =
        c.resumableCount > 0
          ? ` and resume ${
              c.resumableCount === 1 ? "its Claude session" : `${c.resumableCount} Claude sessions`
            }`
          : "";
      const ok = await confirmDialog({
        title: `Restore ${c.title}?`,
        body: `Reopen this session's ${panes}${agents}.`,
        confirmLabel: "Restore",
        cancelLabel: "Cancel",
      });
      if (ok) send({ type: "session.restore", id: c.id });
    });

    return row;
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
    const metaItems: Array<{
      text: string;
      alert?: boolean;
      turnStart?: number;
      since?: number;
      diff?: { added: number; deleted: number };
    }> = [];
    const ahead = s.ahead > 0 ? ` ↑${s.ahead}` : "";

    if (s.agentState === "working") {
      metaItems.push({ text: `▸ ${s.activityDetail || "working"}`, turnStart: s.turnStartMs });
    } else if (s.agentState === "done") {
      // Lead with live "finished · 2m ago" so the freshness reads first — this
      // is the "your move" section, and how long it's been waiting on you is
      // the most useful signal. Falls back to nothing if the turn-end wasn't
      // stamped (older sessions).
      if (s.doneAtMs > 0) metaItems.push({ text: "finished", since: s.doneAtMs });
      // Color-coded diff (+adds green / −dels red) reads at a glance vs plain
      // text. Rendered as sub-spans in the loop below.
      if (s.linesAdded || s.linesDeleted)
        metaItems.push({ text: "", diff: { added: s.linesAdded, deleted: s.linesDeleted } });
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
        if (mi.diff) {
          // Colored +adds / −dels, reusing the footer's diff palette classes.
          if (mi.diff.added) {
            const add = document.createElement("span");
            add.className = "diff-add";
            add.textContent = `+${mi.diff.added}`;
            span.appendChild(add);
          }
          if (mi.diff.deleted) {
            if (mi.diff.added) span.append(" ");
            const del = document.createElement("span");
            del.className = "diff-del";
            del.textContent = `−${mi.diff.deleted}`;
            span.appendChild(del);
          }
        } else {
          span.textContent = mi.text;
        }
        // Live "· 2m" elapsed appended to the working item; the ticker only
        // rewrites the inner span, leaving the action text untouched.
        if (mi.turnStart && mi.turnStart > 0) {
          span.append(" · ");
          span.appendChild(elapsedSpan(mi.turnStart));
        }
        // Live "finished · 2m ago" on done rows — same ticker, relative form.
        if (mi.since && mi.since > 0) {
          span.append(" · ");
          span.appendChild(agoSpan(mi.since));
        }
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
    // Closing a session stops its panes but ARCHIVES the layout to "Recently
    // closed", so it's recoverable. Still a confirm — it tears down running
    // shells — but no longer the scary "can't be recovered" copy.
    close.addEventListener("click", async (ev) => {
      ev.stopPropagation();
      const panes = s.paneCount === 1 ? "1 pane" : `${s.paneCount} panes`;
      const ok = await confirmDialog({
        title: `Close ${s.title}?`,
        body: `Stops this session's ${panes}. You can reopen it from Recently closed.`,
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

/** Single-stroke "rotate-ccw" restore glyph (Fluent/Lucide family). */
function restoreIcon(): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("width", "13");
  svg.setAttribute("height", "13");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  const poly = document.createElementNS(ns, "polyline");
  poly.setAttribute("points", "1 4 1 10 7 10");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", "M3.51 15a9 9 0 1 0 2.13-9.36L1 10");
  svg.append(poly, path);
  return svg;
}
