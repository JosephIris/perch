// Dashboard = the full-window "where does everything stand" view: one card
// per project (dashboard-model.ts), most urgent first. A card rolls up the
// project's chats — each with its threads counted by state and the ones that
// need you named — its other sessions and its email tabs; under the cards,
// the inbox. Opened from the sidebar's Dashboard button or Ctrl+Shift+A; Esc
// or the ✕ closes it.
//
// It points, it doesn't act: every row takes you to the tab (or thread, or
// the inbox) where you answer, merge or reply. The sidebar badge counts what
// is blocked on you.

import type { SessionView, ProjectView, InboxStateMessage, InboxItemView } from "./bridge.js";
import { agentGlyph } from "./agent-glyph.js";
import { send } from "./bridge.js";
import { elapsedSpan, agoSpan } from "./elapsed.js";
import { closeTeamRoom } from "./team-room.js";
import { statusLine, groupOf, icon } from "./thread-ui.js";
import { buildBoard, sessionNeedsYou, type ProjectCard, type ChatBlock } from "./dashboard-model.js";
import { inboxSummary } from "./inbox-summary.js";

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

export class Dashboard {
  private readonly root: HTMLElement;
  private readonly badge: HTMLElement;
  private last: SessionView[] = [];
  private projects: ProjectView[] = [];
  private inbox: InboxStateMessage | null = null;
  /** Opens the inbox (main.ts owns it). */
  onOpenInbox: () => void = () => {};

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

  setProjects(projects: ProjectView[]) { this.projects = projects; }

  setInbox(msg: InboxStateMessage) {
    this.inbox = msg;
    if (this.isOpen()) this.render(this.last);
  }

  /** Go to a tab: select it and close the dashboard (and the team room). */
  private navigate(id: string) {
    closeTeamRoom();
    send({ type: "session.select", id });
    this.hide();
  }

  /** Push the latest state. Updates the needs-you badge always; rebuilds the
   *  body only while open (cheap when closed). */
  render(sessions: SessionView[]) {
    this.last = sessions;
    const needsCount = sessions.filter(sessionNeedsYou).length;
    this.badge.textContent = String(needsCount);
    this.badge.style.display = needsCount > 0 ? "" : "none";
    if (!this.isOpen()) return;

    const { cards, quiet } = buildBoard(sessions, this.projects);
    const needs = cards.reduce((a, c) => a + c.needs, 0);
    const working = cards.reduce((a, c) => a + c.working, 0);
    const ready = cards.reduce((a, c) => a + c.ready, 0);

    const frag = document.createDocumentFragment();
    const head = el("div", "dash__head");
    head.appendChild(el("div", "dash__title", "Dashboard"));
    const counts = el("div", "dash__counts");
    counts.appendChild(el("span", `dash__count dash__count--${needs ? "alert" : "muted"}`, `${needs} need you`));
    if (ready) counts.appendChild(el("span", "dash__count dash__count--ready", `${ready} ready for review`));
    counts.appendChild(el("span", "dash__count dash__count--work", `${working} working`));
    head.appendChild(counts);
    const close = el("button", "dash__close");
    close.setAttribute("aria-label", "Close (Esc)");
    close.appendChild(icon("close"));
    close.addEventListener("click", () => this.hide());
    head.appendChild(close);
    frag.appendChild(head);

    if (!needs) frag.appendChild(el("div", "dash__clear", "All clear — nothing is waiting on you."));

    const grid = el("div", "dash__projects");
    for (const c of cards) grid.appendChild(this.projectCard(c));
    frag.appendChild(grid);
    if (!cards.length) frag.appendChild(el("div", "dash__clear", "Nothing open. Start a session or a project chat from the sidebar."));
    if (quiet) frag.appendChild(el("div", "dash__quiet", `${plural(quiet, "other project")} with nothing open.`));

    const strip = this.inboxStrip();
    if (strip) frag.appendChild(strip);

    this.root.replaceChildren(frag);
  }

  // ---- a project ------------------------------------------------------------

  private projectCard(c: ProjectCard): HTMLElement {
    const card = el("section", "pcard");
    card.dataset.status = c.needs ? "needs" : c.working ? "working" : "clear";
    const head = el("header", "pcard__head");
    head.appendChild(el("h2", "pcard__name", c.name));
    const status = c.needs ? `${c.needs} need${c.needs === 1 ? "s" : ""} you` : c.working ? `${c.working} working` : c.ready ? `${c.ready} ready` : "All clear";
    head.appendChild(el("span", "pcard__status", status));
    card.appendChild(head);

    for (const b of c.chats) card.appendChild(this.chatBlock(b));
    if (c.sessions.length) card.appendChild(this.section("Sessions", c.sessions.map((s) => this.sessionRow(s))));
    if (c.email.length) card.appendChild(this.section("Email", c.email.map((s) => this.emailRow(s))));
    if (c.asleep) card.appendChild(el("div", "pcard__foot", `${plural(c.asleep, "tab")} asleep`));
    return card;
  }

  private section(label: string, rows: HTMLElement[]): HTMLElement {
    const s = el("div", "pcard__section");
    s.appendChild(el("div", "pcard__label", label));
    s.append(...rows);
    return s;
  }

  private row(id: string, cls = ""): HTMLButtonElement {
    const r = el("button", `prow ${cls}`.trim()) as HTMLButtonElement;
    r.type = "button";
    r.addEventListener("click", () => this.navigate(id));
    return r;
  }

  /** A project chat: its title and state, its threads counted by state, and
   *  the threads that need you (or are ready to merge) named. */
  private chatBlock(b: ChatBlock): HTMLElement {
    const wrap = el("div", "pcard__section");
    const r = this.row(b.chat.id, "prow--chat");
    r.appendChild(icon("bubble"));
    r.appendChild(el("span", "prow__title", b.chat.title));
    const tally = el("span", "prow__tally");
    const add = (n: number, group: string, word: string) => {
      if (!n) return;
      const t = el("span", "prow__count");
      t.dataset.group = group;
      t.append(el("span", "pc-dot"), `${n} ${word}`);
      tally.appendChild(t);
    };
    add(b.counts.waiting, "waiting", "need you");
    add(b.counts.working, "working", "working");
    add(b.counts.ready, "ready", "to merge");
    const total = b.counts.waiting + b.counts.working + b.counts.ready + b.counts.idle;
    if (!total) tally.appendChild(el("span", "prow__count", b.chat.agentState === "working" ? "Thinking…" : "No open threads"));
    else if (!b.counts.waiting && !b.counts.working && !b.counts.ready) tally.appendChild(el("span", "prow__count", `${plural(total, "thread")} idle`));
    r.appendChild(tally);
    wrap.appendChild(r);

    for (const t of b.attention.slice(0, 4)) {
      const tr = this.row(t.id, "prow--thread");
      tr.dataset.group = groupOf(t);
      tr.appendChild(el("span", "pc-dot"));
      tr.appendChild(el("span", "prow__num", `#${t.threadNumber ?? ""}`));
      tr.appendChild(el("span", "prow__title", t.title));
      const line = statusLine(t);
      tr.appendChild(el("span", `prow__note${line.blocked ? " prow__note--blocked" : ""}`, line.text));
      wrap.appendChild(tr);
    }
    if (b.attention.length > 4) wrap.appendChild(el("div", "pcard__more", `and ${b.attention.length - 4} more — open the chat's Overview`));
    return wrap;
  }

  private sessionRow(s: SessionView): HTMLElement {
    const r = this.row(s.id);
    const dot = el("span", "card__dot");
    dot.dataset.state = s.agentState;
    r.appendChild(dot);
    for (const agent of (s.agents ?? []).slice(0, 1)) {
      const g = agentGlyph(agent);
      if (g) { g.classList.add("prow__agent"); r.appendChild(g); }
    }
    r.appendChild(el("span", "prow__title", s.title));
    const note = el("span", "prow__note");
    if (sessionNeedsYou(s)) {
      note.classList.add("prow__note--blocked");
      note.textContent = (s.notification?.text || (s.agentState === "permission" ? "Needs your permission" : "Waiting for you")).split("\n")[0];
    } else if (s.agentState === "working" && s.turnStartMs > 0) {
      note.append("Working · ", elapsedSpan(s.turnStartMs));
    } else if (s.agentState === "done" && s.doneAtMs > 0) {
      note.append("Finished ", agoSpan(s.doneAtMs));
    } else note.textContent = s.lastActivity || "";
    r.appendChild(note);
    if (s.ahead > 0) {
      const a = el("span", "prow__ahead", `↑${s.ahead}`);
      a.title = `${plural(s.ahead, "commit")} to push`;
      r.appendChild(a);
    }
    return r;
  }

  private emailRow(s: SessionView): HTMLElement {
    const r = this.row(s.id);
    r.appendChild(mailIcon());
    r.appendChild(el("span", "prow__title", s.title));
    const note = el("span", "prow__note");
    if (sessionNeedsYou(s)) { note.classList.add("prow__note--blocked"); note.textContent = "Waiting for you"; }
    else if (s.agentState === "working") note.textContent = "Working";
    r.appendChild(note);
    return r;
  }

  // ---- the inbox ------------------------------------------------------------

  private inboxStrip(): HTMLElement | null {
    const msg = this.inbox;
    if (!msg?.enabled) return null;
    const s = inboxSummary(msg);
    const strip = el("section", "pcard pcard--inbox");
    const head = el("button", "pcard__head pcard__head--button") as HTMLButtonElement;
    head.type = "button";
    head.addEventListener("click", () => { this.hide(); this.onOpenInbox(); });
    const name = el("h2", "pcard__name");
    name.append(mailIcon(), "Inbox");
    head.appendChild(name);
    const cue = el("span", "inbox-cue");
    if (s.pending) cue.appendChild(el("span", "inbox-cue__pending", `${s.pending} pending`));
    if (s.fresh) cue.appendChild(el("span", "inbox-cue__new", `${s.fresh} new`));
    if (!s.fresh && !s.pending) cue.appendChild(el("span", "pcard__status", "Nothing new"));
    head.appendChild(cue);
    strip.appendChild(head);
    if (s.problem) strip.appendChild(el("div", "pcard__warn",
      s.problem === "login" ? "gcloud needs you to sign in again — the inbox has stopped syncing." : "The last sync failed — open the inbox to retry."));

    const rank = (i: InboxItemView) => (i.state === "new" ? 0 : i.state === "pending" ? 1 : 2);
    const top = msg.items.filter((i) => i.state === "new" || i.state === "pending").sort((a, b) => rank(a) - rank(b)).slice(0, 5);
    for (const item of top) {
      const r = el("button", "prow prow--mail") as HTMLButtonElement;
      r.type = "button";
      r.dataset.state = item.state;
      r.addEventListener("click", () => {
        if (item.sessionId && this.last.some((x) => x.id === item.sessionId)) this.navigate(item.sessionId);
        else { this.hide(); this.onOpenInbox(); }
      });
      r.appendChild(el("span", "prow__mail-dot"));
      r.appendChild(el("span", "prow__from", item.from || "(unknown sender)"));
      r.appendChild(el("span", "prow__title", item.subject));
      r.appendChild(el("span", "prow__note", item.state === "new" ? "New" : "Pending"));
      strip.appendChild(r);
    }
    return strip;
  }
}

function mailIcon(): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 16 16");
  svg.setAttribute("width", "14");
  svg.setAttribute("height", "14");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("class", "pc-icon");
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", "M2.5 4.5h11v7h-11zM3 5l5 3.5L13 5");
  p.setAttribute("fill", "none");
  p.setAttribute("stroke", "currentColor");
  p.setAttribute("stroke-width", "1.3");
  p.setAttribute("stroke-linejoin", "round");
  svg.appendChild(p);
  return svg;
}
