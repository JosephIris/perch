// Dashboard = the full-window view of the attention center. Same derived
// state as the sidebar (the host's SessionView[]), rendered as cards grouped
// into Needs you / Active / Idle. "Idle" = sessions whose agent finished its
// turn ("done") or is dormant — at rest, your move, nothing blocked — distinct
// from "Needs you" (blocked on a permission and can't proceed). Opened from the
// ▦ button in the sidebar or Ctrl+Shift+A; Esc or the ✕ closes it.
//
// Every card is clickable → selects that project + closes (navigate). Waiting
// cards additionally show a "peek" (the agent's ask) and, when the ask is
// simple enough to answer blind, an inline quick-reply that sends straight to
// the pane via pane.in. When the ask is complex (long / code / multi-step),
// the reply is withheld and only "Open to reply" is offered.

import type { PaneTreeView, SessionView, AgentStateName } from "./bridge.js";
import { send, bytesToB64 } from "./bridge.js";
import { elapsedSpan, agoSpan } from "./elapsed.js";
import { openCommitsLightbox } from "./commits-view.js";

const enc = new TextEncoder();

// Peek collapses to this many lines; "show more" reveals up to PEEK_MAX.
const PEEK_COLLAPSED = 2;
const PEEK_MAX = 6;

// A waiting ask is "complex" (→ open-only, no inline reply) when it's long,
// spans many lines, or contains a fenced code block / diff. Tunable.
const COMPLEX_CHARS = 180;
const COMPLEX_LINES = 4;

const STATE_WORD: Record<AgentStateName, string> = {
  waiting: "waiting",
  permission: "needs permission",
  working: "working",
  done: "idle",
  idle: "idle",
};

const el = (tag: string, cls?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  return e;
};

/** Flatten a pane tree to its leaves. */
function leaves(node: PaneTreeView): Array<Extract<PaneTreeView, { kind: "leaf" }>> {
  if (node.kind === "leaf") return [node];
  return node.children.flatMap(leaves);
}

/** The pane a reply should target: the first one that needs you, else the first. */
function replyPaneId(s: SessionView): string | null {
  const ls = leaves(s.rootPane);
  return (ls.find((l) => l.agentState === "waiting" || l.agentState === "permission") ?? ls[0])?.paneId ?? null;
}

/** Sum of commits-since-baseline across the session's panes. */
function commitsTotal(s: SessionView): number {
  return leaves(s.rootPane).reduce((a, l) => a + (l.commitCount || 0), 0);
}

/** The pane to fetch the unpushed-commit recap from: the leaf whose ahead
 *  count matches the session's (the one the ↑N badge represents), else the
 *  first leaf. */
function aheadPaneId(s: SessionView): string | null {
  const ls = leaves(s.rootPane);
  return (ls.find((l) => l.ahead === s.ahead && s.ahead > 0) ?? ls[0])?.paneId ?? null;
}

function isComplexAsk(text: string): boolean {
  if (!text) return false;
  if (text.length > COMPLEX_CHARS) return true;
  if (text.split("\n").length > COMPLEX_LINES) return true;
  if (text.includes("```")) return true;
  return false;
}

export class Dashboard {
  private readonly root: HTMLElement;
  private readonly badge: HTMLElement;
  private last: SessionView[] = [];

  constructor(root: HTMLElement, badge: HTMLElement) {
    this.root = root;
    this.badge = badge;
  }

  isOpen(): boolean {
    return document.body.classList.contains("show-dashboard");
  }
  show() {
    document.body.classList.add("show-dashboard");
    this.root.setAttribute("aria-hidden", "false");
    this.render(this.last);
  }
  hide() {
    document.body.classList.remove("show-dashboard");
    this.root.setAttribute("aria-hidden", "true");
  }
  toggle() {
    this.isOpen() ? this.hide() : this.show();
  }

  /** Navigate to a project: select it in the host and close the dashboard. */
  private navigate(id: string) {
    send({ type: "session.select", id });
    this.hide();
  }

  /** Push the latest state. Updates the waiting badge always; rebuilds the
   *  dashboard body only while it's open (cheap when closed). */
  render(sessions: SessionView[]) {
    this.last = sessions;
    const needsCount = sessions.filter(
      (s) => s.agentState === "waiting" || s.agentState === "permission"
    ).length;
    this.badge.textContent = String(needsCount);
    this.badge.style.display = needsCount > 0 ? "" : "none";
    if (!this.isOpen()) return;

    const needs = sessions.filter(
      (s) => s.agentState === "waiting" || s.agentState === "permission"
    );
    const working = sessions.filter((s) => s.agentState === "working");
    // "Idle" folds the finished-turn ("done") and dormant ("idle") sessions
    // together — both are at rest, your move, nothing blocked.
    const idle = sessions.filter(
      (s) => s.agentState === "done" || s.agentState === "idle"
    );

    const frag = document.createDocumentFragment();

    // Head: title + live count pills + close.
    const head = el("div", "dash__head");
    const title = el("div", "dash__title");
    title.textContent = "Projects";
    head.appendChild(title);
    const counts = el("div", "dash__counts");
    counts.appendChild(this.countPill(`${needs.length} need you`, needs.length ? "alert" : "muted"));
    counts.appendChild(this.countPill(`${working.length} working`, "work"));
    counts.appendChild(this.countPill(`${idle.length} idle`, "muted"));
    head.appendChild(counts);
    const close = el("button", "dash__close");
    close.setAttribute("aria-label", "Close (Esc)");
    close.innerHTML =
      '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4"><path d="M4 4l8 8M12 4l-8 8" stroke-linecap="round"/></svg>';
    close.addEventListener("click", () => this.hide());
    head.appendChild(close);
    frag.appendChild(head);

    // Needs you (with All-clear empty state).
    frag.appendChild(this.groupLabel("Needs you"));
    if (needs.length) {
      const grid = el("div", "dash__grid");
      for (const s of needs) grid.appendChild(this.card(s));
      frag.appendChild(grid);
    } else {
      frag.appendChild(this.emptyCard("All clear - nothing needs you right now."));
    }

    if (working.length) {
      frag.appendChild(this.groupLabel("Active"));
      const grid = el("div", "dash__grid");
      for (const s of working) grid.appendChild(this.card(s));
      frag.appendChild(grid);
    }
    if (idle.length) {
      frag.appendChild(this.groupLabel("Idle"));
      const grid = el("div", "dash__grid");
      for (const s of idle) grid.appendChild(this.card(s));
      frag.appendChild(grid);
    }

    this.root.replaceChildren(frag);
  }

  private countPill(text: string, variant: "alert" | "work" | "muted"): HTMLElement {
    const p = el("span", `dash__count dash__count--${variant}`);
    p.textContent = text;
    return p;
  }

  private groupLabel(text: string): HTMLElement {
    const l = el("div", "dash__group-label");
    l.textContent = text;
    return l;
  }

  private emptyCard(text: string): HTMLElement {
    const e = el("div", "card card--empty");
    e.textContent = `✓ ${text}`;
    return e;
  }

  private card(s: SessionView): HTMLElement {
    const c = el("div", "card");
    c.addEventListener("click", () => this.navigate(s.id));

    const head = el("div", "card__head");
    const dot = el("span", "card__dot");
    dot.dataset.state = s.agentState;
    head.appendChild(dot);
    const t = el("span", "card__title");
    t.textContent = s.title;
    head.appendChild(t);
    const open = el("span", "card__open");
    open.textContent = "Open →";
    head.appendChild(open);
    c.appendChild(head);

    if (s.agentState === "waiting" || s.agentState === "permission") {
      this.renderWaitingBody(c, s, dot);
    } else {
      const atRest = s.agentState === "idle" || s.agentState === "done";
      const note = el("div", "card__note" + (atRest ? " card__note--idle" : ""));
      note.textContent =
        s.notification?.text ||
        (s.agentState === "working"
          ? s.activityDetail || "Working…"
          : s.agentState === "done"
          ? "Idle — your move, no rush."
          : "No recent activity");
      c.appendChild(note);
    }

    // Footer: last-activity + code chips (branch / +commits / ports / panes).
    const foot = el("div", "card__foot");
    const act = el("span", "card__activity");
    act.textContent = STATE_WORD[s.agentState];
    // Live elapsed for a working session; live relative-ago for a finished
    // one (ticks on the page, no host re-push); else the host's last-activity
    // string as a fallback for rows without a stamped turn-end.
    if (s.agentState === "working" && s.turnStartMs > 0) {
      act.append(" · ");
      act.appendChild(elapsedSpan(s.turnStartMs));
    } else if (s.agentState === "done" && s.doneAtMs > 0) {
      act.append(" · ");
      act.appendChild(agoSpan(s.doneAtMs));
    } else if (s.lastActivity) {
      act.append(` · ${s.lastActivity}`);
    }
    foot.appendChild(act);
    if (s.paneCount > 1) {
      const ch = el("span", "chip" + (s.waitingCount > 0 ? " chip--alert" : ""));
      ch.textContent = `${s.paneCount} panes` + (s.workingCount > 0 ? ` · ${s.workingCount} working` : "");
      foot.appendChild(ch);
    }
    if (s.branch) {
      const ch = el("span", "chip");
      ch.textContent = `⎇ ${s.branch}`;
      foot.appendChild(ch);
    }
    const commits = commitsTotal(s);
    if (commits > 0) {
      const ch = el("span", "chip chip--commits");
      ch.textContent = `+${commits} commit${commits === 1 ? "" : "s"}`;
      foot.appendChild(ch);
    }
    // Diff footprint since baseline — added (green) / deleted (red) / files.
    if (s.linesAdded || s.linesDeleted) {
      const ch = el("span", "chip chip--diff");
      if (s.linesAdded) {
        const a = el("span", "diff-add");
        a.textContent = `+${s.linesAdded}`;
        ch.appendChild(a);
      }
      if (s.linesDeleted) {
        const d = el("span", "diff-del");
        d.textContent = `−${s.linesDeleted}`;
        ch.appendChild(d);
      }
      if (s.filesChanged) {
        const f = el("span", "diff-files");
        f.textContent = `${s.filesChanged} file${s.filesChanged === 1 ? "" : "s"}`;
        ch.appendChild(f);
      }
      foot.appendChild(ch);
    }
    // Unpushed commits — click opens the recap lightbox (stop-propagation so
    // it doesn't also navigate to the session via the card's click handler).
    if (s.ahead > 0) {
      const ch = el("span", "chip chip--ahead");
      ch.textContent = `↑${s.ahead}`;
      ch.title = "Commits ready to push — click for recap";
      const pid = aheadPaneId(s);
      if (pid) {
        ch.addEventListener("click", (ev) => {
          ev.stopPropagation();
          openCommitsLightbox(pid);
        });
      }
      foot.appendChild(ch);
    }
    for (const port of s.ports ?? []) {
      const ch = el("span", "chip");
      ch.textContent = `:${port}`;
      foot.appendChild(ch);
    }
    c.appendChild(foot);

    return c;
  }

  /** Waiting card body: a peek at the agent's ask, then either an inline
   *  reply (simple ask) or an Open-to-reply button (complex ask). */
  private renderWaitingBody(c: HTMLElement, s: SessionView, dot: HTMLElement) {
    const wrap = el("div", "card__wait");
    const ask =
      s.notification?.text ||
      (s.agentState === "permission" ? "Needs your permission" : "Waiting for your input");
    const lines = ask.split("\n").map((l) => l.trim()).filter(Boolean).slice(-PEEK_MAX);

    const peek = el("div", "card__peek");
    lines.forEach((line, i, arr) => {
      const ln = el("div", "card__peek-line" + (i === arr.length - 1 ? " card__peek-line--q" : ""));
      ln.textContent = line;
      peek.appendChild(ln);
    });
    const collapsible = lines.length > PEEK_COLLAPSED;
    if (collapsible) peek.classList.add("card__peek--collapsed");
    wrap.appendChild(peek);
    if (collapsible) {
      const more = el("button", "card__peek-more");
      more.textContent = "Show more";
      more.addEventListener("click", (ev) => {
        ev.stopPropagation();
        const col = peek.classList.toggle("card__peek--collapsed");
        more.textContent = col ? "Show more" : "Show less";
      });
      wrap.appendChild(more);
    }

    const paneId = replyPaneId(s);
    if (isComplexAsk(ask) || !paneId) {
      // Too much to answer from a peek → only offer to open the pane.
      const ob = el("button", "card__openbtn");
      ob.textContent = "Open to reply →";
      ob.addEventListener("click", (ev) => {
        ev.stopPropagation();
        this.navigate(s.id);
      });
      wrap.appendChild(ob);
    } else {
      const form = document.createElement("form");
      form.className = "card__reply";
      const inp = document.createElement("input");
      inp.className = "card__reply-input";
      inp.type = "text";
      inp.placeholder = "Type your reply…";
      inp.spellcheck = false;
      const btn = document.createElement("button");
      btn.className = "card__reply-send";
      btn.type = "submit";
      btn.setAttribute("aria-label", "Send");
      btn.innerHTML =
        '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M3 8h9M8 4l4 4-4 4" stroke-linecap="round" stroke-linejoin="round"/></svg>';
      [form, inp, btn].forEach((e) => e.addEventListener("click", (ev) => ev.stopPropagation()));
      form.append(inp, btn);
      form.addEventListener("submit", (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        const v = inp.value.trim();
        if (!v) return;
        // Send the reply + Enter straight to the waiting pane.
        send({ type: "pane.in", paneId, b64: bytesToB64(enc.encode(v + "\r")) });
        dot.dataset.state = "working";
        const sent = el("div", "card__note");
        sent.textContent = "You replied · working on it…";
        wrap.replaceChildren(sent);
      });
      wrap.appendChild(form);
    }

    c.appendChild(wrap);
  }
}
