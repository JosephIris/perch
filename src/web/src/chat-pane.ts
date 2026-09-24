// ChatPane = a project chat: the conversation with its coordinator Claude,
// and beside it the Overview of its threads (chat-overview.ts). Not a
// terminal — it is the one place in Perch you talk to Claude as a
// conversation, so it looks like one.
//
// The host owns the conversation (ChatController): the page asks for the
// history once, then receives each row as it is written (chat.entry), whether
// a turn is running (chat.status), and the chat's goal, instructions, memory
// and proposed threads (chat.meta).
//
// How rows are drawn, following Claude's own project chats:
//   * your messages as bubbles, with "Sent to N threads" under one that the
//     coordinator passed on;
//   * Claude's prose as Markdown, a thread named "#3" drawn as that thread's
//     tag (hover for its state, click to open it), and under each finished
//     reply its time and a copy button;
//   * the tools it used folded into one quiet line; its `perch thread`
//     commands not at all — what they did shows as cards;
//   * a started thread as a live card; proposed threads as one "Start new
//     threads" card; a thread waiting on you as a card with View thread;
//   * a wavy rule where the day changes.

import { send } from "./bridge.js";
import type { PaneTreeView, SessionView, ChatEntryView, ChatMetaMessage, ThreadEventView, ThreadTaskView } from "./bridge.js";
import { renderMarkdown } from "./md.js";
import { ChatOverview, divider } from "./chat-overview.js";
import {
  groupOf, statusLine, askText, firstSentence, splitThreadRefs, summarizeWork, modelLabel, dayLabel, dayKey,
  el, button, icon, ring, setRing, threadChip, fillChip, hidePop, reducedMotion,
} from "./thread-ui.js";
import { copyText } from "./clipboard.js";
import { elapsedSpan } from "./elapsed.js";

const EASE = "cubic-bezier(0, 0, 0, 1)";

/** A run of Claude's rows between two things that aren't Claude's: gets a
 *  footer (time, copy) once it is over. */
type Segment = { texts: string[]; lastAt: number; sealed: boolean };

/** Your message and what the coordinator passed on from it. */
type Turn = { user: HTMLElement; marker: HTMLElement | null; sentTo: Set<string> };

type Block = { kind: string; el: HTMLElement; steps?: { verb: string; target: string }[] };

export class ChatPane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly titleEl: HTMLElement;
  private readonly goalEl: HTMLElement;
  private readonly ovBtn: HTMLButtonElement;
  private readonly scroll: HTMLElement;
  private readonly log: HTMLElement;
  private readonly busyEl: HTMLElement;
  private readonly input: HTMLTextAreaElement;
  private readonly sendBtn: HTMLButtonElement;
  private readonly modelEl: HTMLElement;
  private readonly spinEl: SVGSVGElement;
  private readonly queuedEl: HTMLElement;
  private readonly overview: ChatOverview;
  private running = false;
  private runningSince = 0;
  private loaded = false;
  private sessionId = "";
  private threads: SessionView[] = [];
  private meta: ChatMetaMessage | null = null;
  private readonly seen = new Set<string>();
  // Rendering state, rebuilt with the history.
  private last: Block | null = null;
  private segment: Segment | null = null;
  private turn: Turn | null = null;
  private lastDay = "";
  /** Claude's rows naming "#n" before that thread was known: redrawn once it is. */
  private unresolved: { el: HTMLElement; text: string; notice?: boolean }[] = [];
  private readonly threadCards = new Map<string, HTMLElement[]>();
  private readonly waitCards = new Map<string, HTMLElement[]>();
  private readonly suggestRows = new Map<string, HTMLElement>();
  /** Suggestions you pressed Start on, until the host says they started. */
  private readonly starting = new Map<string, number>();

  constructor(paneId: string) {
    this.paneId = paneId;
    this.element = el("div", "pane pane--chat");
    this.element.dataset.paneId = paneId;

    const main = el("section", "chat");
    const head = el("header", "chat__head");
    const titles = el("div", "chat__titles");
    this.titleEl = el("div", "chat__title", "Project chat");
    this.goalEl = el("div", "chat__goal");
    titles.append(this.titleEl, this.goalEl);
    this.ovBtn = button("chat__ovbtn", "", () => this.setOverview(this.element.classList.contains("pane--chat-noov")), "Show or hide the Overview");
    this.ovBtn.append(icon("list"), el("span", undefined, "Overview"));
    this.ovBtn.setAttribute("aria-pressed", "true");
    const gear = button("chat__gear", icon("gear"), () => { this.setOverview(true); this.overview.showAbout(); }, "Goal, instructions and memory");
    head.append(titles, this.ovBtn, gear);
    main.appendChild(head);

    this.scroll = el("div", "chat__scroll");
    this.log = el("div", "chat__log");
    this.busyEl = el("div", "chat__busy");
    this.busyEl.hidden = true;
    this.scroll.append(this.log, this.busyEl);
    main.appendChild(this.scroll);

    const compose = el("div", "chat__compose");
    const box = el("div", "pc-composer");
    this.input = document.createElement("textarea");
    this.input.className = "pc-composer__input";
    this.input.rows = 1;
    this.input.placeholder = "Ask Claude a question or start a task…";
    this.input.setAttribute("aria-label", "Message the project chat");
    this.input.addEventListener("input", () => this.autosize());
    this.input.addEventListener("keydown", (ev) => {
      // Enter sends; Shift+Enter is a new line. The box owns its keys.
      ev.stopPropagation();
      if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); this.submit(); }
    });
    this.sendBtn = button("pc-composer__send", icon("enter"), () => {
      if (this.running && this.input.value.trim() === "") send({ type: "chat.stop", paneId: this.paneId });
      else this.submit();
    }, "Send");
    box.append(this.input, this.sendBtn);
    const tools = el("div", "chat__toolbar");
    this.queuedEl = el("span", "chat__queued");
    this.modelEl = el("span", "chat__model");
    this.spinEl = ring();
    setRing(this.spinEl, 0, 0);
    this.spinEl.classList.add("chat__spin");
    tools.append(this.queuedEl, this.modelEl, this.spinEl);
    compose.append(box, tools);
    main.appendChild(compose);

    this.overview = new ChatOverview((text) => this.sendText(text));
    this.overview.onWaiting = (n) => {
      this.ovBtn.classList.toggle("chat__ovbtn--dot", n > 0);
      this.ovBtn.title = n > 0 ? `${n} thread${n === 1 ? "" : "s"} waiting on you` : "Show or hide the Overview";
    };
    this.overview.onClose = () => this.setOverview(false);

    const body = el("div", "chat__body");
    body.append(main, this.overview.element);
    this.element.appendChild(body);
    this.element.addEventListener("mousedown", () => send({ type: "pane.focus", paneId: this.paneId }));
    this.scroll.addEventListener("scroll", () => hidePop(true), { passive: true });
    this.renderEmpty();
    this.updateButton();
  }

  attach(host: HTMLElement) {
    host.appendChild(this.element);
    this.autosize();
    if (!this.loaded) send({ type: "chat.request", paneId: this.paneId });
  }
  dispose() { hidePop(true); this.overview.dispose(); this.element.remove(); }
  setName(_name: string) { /* the chat is titled by its tab */ }
  setActive(active: boolean) { this.element.classList.toggle("pane--active", active); }
  focus() { this.input.focus(); }
  feed(_b64: string) { /* no terminal */ }
  notifyExit(_code: number) { /* nothing to exit */ }
  forceRefit() { /* flows like a document */ }
  changeFontSize(): number { return 0; }
  resetFontSize(): number { return 0; }

  applyLeafView(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    this.element.dataset.color = String(leaf.colorIndex);
  }

  /** The tab this chat is and its threads, from every state push. */
  setSession(session: SessionView | undefined, threads: SessionView[]) {
    if (session) {
      this.sessionId = session.id;
      this.titleEl.textContent = session.title;
      this.goalEl.textContent = session.chatGoal ?? "";
      this.goalEl.hidden = !session.chatGoal;
    }
    const known = new Set(this.threads.map((t) => t.threadNumber));
    this.threads = threads;
    this.overview.setThreads(threads);
    for (const [id, cards] of this.threadCards) for (const c of cards) this.fillThreadCard(c, id);
    for (const [id, cards] of this.waitCards) for (const c of cards) this.fillWaitCard(c, id);
    for (const chip of this.log.querySelectorAll<HTMLElement>(".pc-chip")) fillChip(chip, this.byId(chip.dataset.threadId ?? ""));
    // Rows that named a thread before it was known.
    if (this.unresolved.length && threads.some((t) => !known.has(t.threadNumber))) {
      const still: { el: HTMLElement; text: string; notice?: boolean }[] = [];
      for (const r of this.unresolved) {
        if (r.notice) { r.el.replaceChildren(); this.noticeText(r.el, r.text); }
        else r.el.replaceChildren(renderMarkdown(r.text, this.deco));
        if (r.notice ? this.hasUnknownThread(r.text) : this.hasUnknownRef(r.text)) still.push(r);
      }
      this.unresolved = still;
    }
    for (const turn of this.turns) this.fillMarker(turn);
    this.refreshSuggestions();
  }

  // ---- host messages ------------------------------------------------------

  applyHistory(entries: ChatEntryView[], running: boolean, queued: number, model: string) {
    this.loaded = true;
    this.seen.clear();
    this.threadCards.clear();
    this.waitCards.clear();
    this.suggestRows.clear();
    this.turns = [];
    this.unresolved = [];
    this.last = null;
    this.segment = null;
    this.turn = null;
    this.lastDay = "";
    this.log.replaceChildren();
    for (const e of entries) this.addEntry(e, false);
    if (!entries.length) this.renderEmpty();
    this.applyStatus(running, queued, model);
    this.refreshSuggestions();
    this.toBottom(true, false);
  }

  applyEntry(e: ChatEntryView) {
    if (!this.loaded) return;               // the history will carry it
    const stick = this.nearBottom();
    this.log.querySelector(".chat__welcome")?.remove();
    this.addEntry(e, true);
    this.refreshSuggestions();
    this.toBottom(stick || e.kind === "user", true);
  }

  applyStatus(running: boolean, queued: number, model: string) {
    if (running && !this.running) this.runningSince = Date.now();
    this.running = running;
    if (model) this.modelEl.textContent = modelLabel(model);
    this.spinEl.classList.toggle("chat__spin--on", running);
    this.queuedEl.textContent = queued > 0 ? `${queued} more waiting` : "";
    this.busyEl.hidden = !running;
    if (running) {
      const r = ring(); setRing(r, 0, 0);
      this.busyEl.replaceChildren(r, el("span", undefined, "Working · "), elapsedSpan(this.runningSince));
    } else {
      this.sealSegment();
    }
    this.updateButton();
    if (running) this.toBottom(this.nearBottom(), true);
  }

  applyMeta(meta: ChatMetaMessage) {
    this.meta = meta;
    this.overview.setMeta(meta);
    for (const id of [...this.starting.keys()]) {
      const s = meta.suggestions.find((x) => x.id === id);
      if (!s || s.threadId) this.starting.delete(id);
    }
    this.refreshSuggestions();
    this.renderEmpty();
  }

  applyThreadTranscript(id: string, events: ThreadEventView[], tasks: ThreadTaskView[]) {
    this.overview.applyTranscript(id, events, tasks);
  }

  // ---- rendering ----------------------------------------------------------

  private turns: Turn[] = [];

  private byId(id: string): SessionView | undefined { return this.threads.find((t) => t.id === id); }
  private byNumber(n: number): SessionView | undefined { return this.threads.find((t) => t.threadNumber === n); }

  /** Claude's text with "#n" drawn as thread n's tag. */
  private readonly deco = (host: HTMLElement, text: string) => {
    for (const part of splitThreadRefs(text, (n) => !!this.byNumber(n))) {
      if (typeof part === "string") { host.append(part); continue; }
      const t = this.byNumber(part)!;
      host.appendChild(threadChip(t.id, t.title, (id) => this.byId(id), (id) => this.openThread(id)));
    }
  };

  private hasUnknownThread(text: string): boolean {
    for (const m of text.matchAll(/Thread (\d+) \(/g)) if (!this.byNumber(Number(m[1]))) return true;
    return false;
  }

  private hasUnknownRef(text: string): boolean {
    for (const m of text.matchAll(/(^|[^\w&#/])#(\d{1,4})\b/g)) if (!this.byNumber(Number(m[2]))) return true;
    return false;
  }

  private addEntry(e: ChatEntryView, live: boolean) {
    if (this.seen.has(e.id)) return;
    this.seen.add(e.id);

    // Tools: `perch thread` is how the coordinator acts on threads — shown
    // by the cards it makes, not as commands. The rest fold into one line.
    if (e.kind === "tool") {
      const cmd = e.text.match(/^perch(?:\.exe)?\s+thread\s+(\w+)\s*("?)(\w+)?\2/);
      if (cmd) {
        if (cmd[1] === "send" && cmd[3] && /^\d+$/.test(cmd[3])) this.noteSent(`n:${cmd[3]}`);
        return;
      }
      if (this.last?.kind === "work" && this.last.steps) {
        this.last.steps.push({ verb: e.tool, target: e.text });
        this.fillWork(this.last);
        return;
      }
      const d = document.createElement("details");
      d.className = "pc-work";
      const block: Block = { kind: "work", el: d, steps: [{ verb: e.tool, target: e.text }] };
      this.fillWork(block);
      this.append(block, e, live);
      return;
    }

    switch (e.kind) {
      case "user": {
        this.sealSegment();
        const row = el("div", "chat-row chat-row--user");
        row.appendChild(el("div", "pc-bubble", e.text));
        this.append({ kind: "user", el: row }, e, live);
        const turn: Turn = { user: row, marker: null, sentTo: new Set() };
        this.turns.push(turn);
        this.turn = turn;
        return;
      }
      case "claude": {
        const row = el("div", "chat-row chat-row--claude md");
        row.appendChild(renderMarkdown(e.text, this.deco));
        if (this.hasUnknownRef(e.text)) this.unresolved.push({ el: row, text: e.text });
        if (!this.segment || this.segment.sealed) this.segment = { texts: [], lastAt: 0, sealed: false };
        this.segment.texts.push(e.text);
        this.segment.lastAt = e.atMs;
        this.append({ kind: "claude", el: row }, e, live);
        return;
      }
      case "thread": {
        const id = e.tool.replace(/^thread:/, "");
        const card = button("chat-thread", "", () => this.openThread(id)) as HTMLElement;
        card.dataset.title = e.text;
        const list = this.threadCards.get(id) ?? [];
        list.push(card);
        this.threadCards.set(id, list);
        this.fillThreadCard(card, id);
        this.noteSent(`id:${id}`);
        this.append({ kind: "thread", el: card }, e, live);
        return;
      }
      case "suggest": {
        const id = e.tool.replace(/^suggest:/, "");
        let block = this.last?.kind === "suggest" ? this.last : null;
        if (!block) {
          const card = el("div", "pc-card chat-suggest");
          card.append(el("div", "chat-suggest__head", "Start new threads"), el("div", "chat-suggest__rows"), el("div", "chat-suggest__foot"));
          block = { kind: "suggest", el: card };
          this.append(block, e, live);
        }
        const row = el("div", "chat-suggest__row");
        row.dataset.id = id;
        row.dataset.title = e.text;
        this.suggestRows.set(id, row);
        block.el.querySelector(".chat-suggest__rows")!.appendChild(row);
        this.fillSuggestRow(row);
        this.fillSuggestFoot(block.el);
        return;
      }
      case "notice": {
        this.sealSegment();
        const threadId = e.tool.startsWith("thread:") ? e.tool.slice(7) : "";
        if (threadId) {
          // A thread waiting on you: a card that follows it until answered.
          const card = el("div", "pc-card chat-wait");
          card.dataset.threadId = threadId;
          card.dataset.text = e.text;
          const list = this.waitCards.get(threadId) ?? [];
          list.push(card);
          this.waitCards.set(threadId, list);
          this.fillWaitCard(card, threadId);
          this.append({ kind: "wait", el: card }, e, live);
          return;
        }
        const row = el("div", "chat-row chat-row--notice");
        const text = el("span", "chat-notice__text");
        this.noticeText(text, e.text);
        if (this.hasUnknownThread(e.text)) this.unresolved.push({ el: text, text: e.text, notice: true });
        row.append(icon("reply", "pc-icon chat-notice__mark"), text);
        this.append({ kind: "notice", el: row }, e, live);
        return;
      }
      default: {
        this.sealSegment();
        this.append({ kind: "error", el: el("div", "chat-row chat-row--error", e.text) }, e, live);
      }
    }
  }

  /** Add a block at the end, after a day rule when the day changed. */
  private append(block: Block, e: ChatEntryView, live: boolean) {
    const day = dayKey(e.atMs || Date.now());
    if (day !== this.lastDay) {
      this.lastDay = day;
      this.log.appendChild(divider(dayLabel(e.atMs || Date.now())));
    }
    this.log.appendChild(block.el);
    this.last = block;
    if (live && !reducedMotion())
      block.el.animate([{ opacity: 0, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }], { duration: 200, easing: EASE });
  }

  /** Perch's own line: "Thread 3 (Title) …" drawn with that thread's tag. */
  private noticeText(host: HTMLElement, text: string) {
    const rx = /Thread (\d+) \(([^)]*)\)/g;
    let at = 0;
    for (let m = rx.exec(text); m; m = rx.exec(text)) {
      host.append(text.slice(at, m.index));
      const t = this.byNumber(Number(m[1]));
      if (t) host.appendChild(threadChip(t.id, m[2], (id) => this.byId(id), (id) => this.openThread(id)));
      else host.append(m[2] || `Thread ${m[1]}`);
      at = m.index + m[0].length;
    }
    host.append(text.slice(at));
  }

  private fillWork(block: Block) {
    const steps = block.steps ?? [];
    const line = summarizeWork(steps);
    block.el.hidden = !line;
    const sum = document.createElement("summary");
    sum.textContent = line;
    const list = el("div", "pc-work__list");
    for (const s of steps) {
      const r = el("div", "pc-work__row");
      r.append(el("span", "pc-work__verb", s.verb), el("span", "pc-work__target", s.target));
      list.appendChild(r);
    }
    const open = (block.el as HTMLDetailsElement).open;
    block.el.replaceChildren(sum, list);
    (block.el as HTMLDetailsElement).open = open;
  }

  /** The coordinator passed your message on (started or messaged a thread). */
  private noteSent(key: string) {
    const turn = this.turn;
    if (!turn) return;
    turn.sentTo.add(key);
    this.fillMarker(turn);
  }

  private fillMarker(turn: Turn) {
    // "n:3" and "id:<guid>" may be the same thread; count threads, not keys.
    const threads = new Set<string>();
    for (const k of turn.sentTo) {
      const t = k.startsWith("n:") ? this.byNumber(Number(k.slice(2))) : this.byId(k.slice(3));
      threads.add(t ? t.id : k);
    }
    if (!threads.size) return;
    if (!turn.marker) {
      turn.marker = el("div", "chat-sent");
      // Right under your message.
      turn.user.after(turn.marker);
    }
    const n = threads.size;
    turn.marker.replaceChildren(icon("reply"), el("span", undefined, n === 1 ? "Sent to one thread" : `Sent to ${n} threads`));
  }

  /** A run of Claude's replies is over: its time and a copy button. */
  private sealSegment() {
    const seg = this.segment;
    if (!seg || seg.sealed || !seg.texts.length) return;
    seg.sealed = true;
    const foot = el("div", "chat-foot");
    const copy = button("chat-foot__btn", icon("copy"), () => {
      void copyText(seg.texts.join("\n\n")).then((ok) => {
        if (!ok) return;
        copy.classList.add("chat-foot__btn--done");
        window.setTimeout(() => copy.classList.remove("chat-foot__btn--done"), 1200);
      });
    }, "Copy");
    foot.append(copy, el("span", "chat-foot__time", new Date(seg.lastAt || Date.now()).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })));
    this.log.appendChild(foot);
    this.last = { kind: "foot", el: foot };
  }

  private openThread(id: string) {
    this.setOverview(true);
    this.overview.openThread(id);
  }

  private setOverview(open: boolean) {
    this.element.classList.toggle("pane--chat-noov", !open);
    this.ovBtn.setAttribute("aria-pressed", String(open));
  }

  /** A started thread: dot, title and what it is doing, kept live. */
  private fillThreadCard(card: HTMLElement, id: string) {
    const t = this.byId(id);
    const g = t ? groupOf(t) : "resolved";
    const line = t ? statusLine(t).text : "Closed";
    const sig = `${g}|${t?.title}|${line}`;
    if (card.dataset.sig === sig) return;
    card.dataset.sig = sig;
    card.dataset.group = g;
    card.replaceChildren(el("span", "pc-dot"), el("span", "chat-thread__title", t?.title ?? card.dataset.title ?? ""), el("span", "chat-thread__line", line));
  }

  /** A thread that asked for you: what it needs while it still does. */
  private fillWaitCard(card: HTMLElement, id: string) {
    const t = this.byId(id);
    const stillWaiting = !!t && groupOf(t) === "waiting";
    const line = !t ? "This thread is closed."
      : stillWaiting ? (t.agentState === "permission" ? `It asks to: ${askText(t)}` : (t.notification?.text || "It's waiting for your answer."))
      : "Answered.";
    const sig = `${stillWaiting}|${t?.title}|${line}`;
    if (card.dataset.sig === sig) return;
    card.dataset.sig = sig;
    card.classList.toggle("chat-wait--done", !stillWaiting);
    const head = el("div", "pc-card__head");
    head.append(icon("hand", "pc-icon pc-card__hand"), el("span", "pc-card__title", t?.title ?? (card.dataset.text ?? "")));
    const acts = el("div", "pc-card__actions");
    if (t) acts.appendChild(button(stillWaiting ? "pc-btn pc-btn--primary" : "pc-btn pc-btn--quiet", "View thread", () => this.openThread(id)));
    card.replaceChildren(head, el("div", "pc-card__line", line), acts);
  }

  /** One proposed thread: title, why, and Start (or where it went). */
  private fillSuggestRow(row: HTMLElement) {
    const id = row.dataset.id ?? "";
    const s = this.meta?.suggestions.find((x) => x.id === id);
    const started = s?.threadId ? this.byId(s.threadId) : undefined;
    const state = s?.threadId ? "started" : s?.dismissed ? "dismissed" : this.starting.has(id) ? "starting" : "open";
    const sig = `${state}|${s?.summary ?? ""}|${started?.threadNumber ?? ""}|${started ? groupOf(started) : ""}`;
    if (row.dataset.sig === sig) return;
    const wasOpen = row.dataset.state === "open" || row.dataset.state === "starting";
    row.dataset.sig = sig;
    row.dataset.state = state;
    if (state === "dismissed") {
      // Waved away: fold out of the card, then go.
      if (wasOpen && row.isConnected && !reducedMotion()) {
        const h = row.offsetHeight;
        row.animate([{ height: `${h}px`, opacity: 1 }, { height: "0px", opacity: 0 }], { duration: 180, easing: EASE })
          .finished.then(() => { row.hidden = true; }, () => { row.hidden = true; });
      } else row.hidden = true;
      return;
    }
    row.hidden = false;
    const text = el("div", "chat-suggest__text");
    text.append(el("div", "chat-suggest__title", row.dataset.title ?? ""));
    if (s?.summary) text.appendChild(el("div", "chat-suggest__why", firstSentence(s.summary)));
    const acts = el("div", "chat-suggest__acts");
    if (state === "started") {
      // Where it went: its live state dot and "Started", opening the thread.
      const b = button("pc-btn pc-btn--quiet chat-suggest__went", "", () => s?.threadId && this.openThread(s.threadId), "Open this thread");
      b.dataset.group = started ? groupOf(started) : "resolved";
      b.append(el("span", "pc-dot"), el("span", undefined, "Started"));
      acts.appendChild(b);
    } else {
      const start = button("pc-btn chat-suggest__start", state === "starting" ? "Starting…" : "Start", () => this.start([id]));
      start.disabled = state === "starting";
      acts.append(start, button("chat-suggest__x", icon("close"), () => {
        send({ type: "suggestion.dismiss", sessionId: this.sessionId, id });
      }, "Dismiss"));
    }
    row.replaceChildren(icon("bubble", "pc-icon chat-suggest__icon"), text, acts);
  }

  /** "Start N threads" for the ones in this card still to start. */
  private fillSuggestFoot(card: HTMLElement) {
    const foot = card.querySelector(".chat-suggest__foot") as HTMLElement;
    const open = [...card.querySelectorAll<HTMLElement>(".chat-suggest__row")].filter((r) => r.dataset.state === "open").map((r) => r.dataset.id ?? "");
    const all = [...card.querySelectorAll<HTMLElement>(".chat-suggest__row")];
    card.hidden = all.length > 0 && all.every((r) => r.dataset.state === "dismissed");
    const sig = open.join(",");
    if (foot.dataset.sig === sig) return;
    foot.dataset.sig = sig;
    foot.hidden = open.length < 2;
    foot.replaceChildren(open.length >= 2
      ? button("pc-btn pc-btn--strong", `Start ${open.length} threads`, () => this.start(open))
      : "");
  }

  private start(ids: string[]) {
    const now = Date.now();
    for (const id of ids) this.starting.set(id, now);
    send({ type: "suggestion.start", sessionId: this.sessionId, id: ids.join(",") });
    this.refreshSuggestions();
    // If the host never answers (it failed and said so in a toast), the
    // buttons come back.
    window.setTimeout(() => {
      let changed = false;
      for (const id of ids) if (this.starting.get(id) === now) { this.starting.delete(id); changed = true; }
      if (changed) this.refreshSuggestions();
    }, 60000);
  }

  private refreshSuggestions() {
    for (const row of this.suggestRows.values()) this.fillSuggestRow(row);
    for (const card of this.log.querySelectorAll<HTMLElement>(".chat-suggest")) this.fillSuggestFoot(card);
  }

  private renderEmpty() {
    const existing = this.log.querySelector(".chat__welcome");
    if (this.log.childElementCount && !existing) return;
    const name = this.meta?.userName?.trim();
    const w = el("div", "chat-row chat-row--claude chat__welcome");
    w.append(
      el("p", undefined, `${name ? `Hi ${name}, welcome` : "Welcome"} to your project chat. I coordinate the work here: ask for whatever you need, and I'll either answer you directly or start threads — each a Claude in its own copy of the repo — to work on things in parallel.`),
      el("p", "chat__welcome-more", "I'll post updates here when something finishes or needs you. Tell me how you like to work, too — “propose threads before starting them”, “shorter updates” — and I'll remember it."));
    if (existing) existing.replaceWith(w); else this.log.appendChild(w);
  }

  private sendText(text: string) {
    send({ type: "chat.send", paneId: this.paneId, text });
  }

  private submit() {
    const text = this.input.value.trim();
    if (!text) return;
    this.sendText(text);
    this.input.value = "";
    this.autosize();
  }

  private updateButton() {
    const stop = this.running && this.input.value.trim() === "";
    const want = stop ? "stop" : "enter";
    if (this.sendBtn.dataset.icon !== want) {
      this.sendBtn.dataset.icon = want;
      this.sendBtn.replaceChildren(icon(want));
      this.sendBtn.title = stop ? "Stop" : "Send";
      this.sendBtn.setAttribute("aria-label", this.sendBtn.title);
    }
    this.sendBtn.classList.toggle("pc-composer__send--stop", stop);
    this.sendBtn.classList.toggle("pc-composer__send--ready", !stop && this.input.value.trim() !== "");
  }

  private autosize() {
    this.input.style.height = "auto";
    this.input.style.height = `${Math.min(Math.max(this.input.scrollHeight, 24), 200)}px`;
    this.updateButton();
  }

  private nearBottom(): boolean {
    const s = this.scroll;
    return s.scrollHeight - s.scrollTop - s.clientHeight < 80;
  }

  private toBottom(force: boolean, smooth: boolean) {
    if (!force) return;
    requestAnimationFrame(() => {
      this.scroll.scrollTo({ top: this.scroll.scrollHeight, behavior: smooth && !reducedMotion() ? "smooth" : "auto" });
    });
  }
}
