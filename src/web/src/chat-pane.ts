// ChatPane = a project chat: the conversation with its coordinator Claude,
// and beside it the Overview of its threads (chat-overview.ts). Not a
// terminal — it is the one place in Perch you talk to Claude as a
// conversation, so it looks like one.
//
// The host owns the conversation (ChatController): the page asks for the
// history once, then receives each row as it is written (chat.entry), whether
// a turn is running (chat.status), and the chat's goal, instructions, memory
// and proposed threads (chat.meta). Rows are your messages, Claude's prose
// (Markdown), a compact line per tool it used, Perch's notices, errors, and
// two kinds of card: a thread that was started (live status, opens in the
// Overview) and a thread that was proposed (Start).

import { send } from "./bridge.js";
import type { PaneTreeView, SessionView, ChatEntryView, ChatMetaMessage, ThreadEventView } from "./bridge.js";
import { renderMarkdown } from "./md.js";
import { ChatOverview, groupOf, stateLabel } from "./chat-overview.js";

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

/** A few words for a tool row's verb. */
const TOOL_WORD: Record<string, string> = {
  Read: "Read", Grep: "Searched", Glob: "Listed", LS: "Listed", Bash: "Ran", PowerShell: "Ran",
  WebSearch: "Searched the web", WebFetch: "Fetched", TodoWrite: "Updated its plan",
};

export class ChatPane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly titleEl: HTMLElement;
  private readonly goalEl: HTMLElement;
  private readonly stateEl: HTMLElement;
  private readonly ovBtn: HTMLButtonElement;
  private readonly scroll: HTMLElement;
  private readonly log: HTMLElement;
  private readonly busyEl: HTMLElement;
  private readonly suggestBar: HTMLElement;
  private readonly input: HTMLTextAreaElement;
  private readonly sendBtn: HTMLButtonElement;
  private readonly overview: ChatOverview;
  private running = false;
  private loaded = false;
  private sessionId = "";
  private threads: SessionView[] = [];
  private meta: ChatMetaMessage | null = null;
  private readonly seen = new Set<string>();
  /** Cards in the conversation, kept to update in place. */
  private readonly threadCards = new Map<string, HTMLElement[]>();
  private readonly suggestCards = new Map<string, HTMLElement>();

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
    this.stateEl = el("div", "chat__state");
    this.ovBtn = el("button", "chat__ovbtn", "Overview") as HTMLButtonElement;
    this.ovBtn.type = "button";
    this.ovBtn.setAttribute("aria-pressed", "true");
    this.ovBtn.addEventListener("click", () => {
      const open = this.element.classList.toggle("pane--chat-noov");
      this.ovBtn.setAttribute("aria-pressed", String(!open));
    });
    head.append(titles, this.stateEl, this.ovBtn);
    main.appendChild(head);

    this.scroll = el("div", "chat__scroll");
    this.log = el("div", "chat__log");
    this.busyEl = el("div", "chat__busy");
    this.busyEl.hidden = true;
    this.scroll.append(this.log, this.busyEl);
    main.appendChild(this.scroll);

    this.suggestBar = el("div", "chat__suggestbar");
    this.suggestBar.hidden = true;
    main.appendChild(this.suggestBar);

    const composer = el("div", "chat__composer");
    this.input = document.createElement("textarea");
    this.input.className = "chat__input";
    this.input.rows = 1;
    this.input.setAttribute("aria-label", "Message the project chat");
    this.input.addEventListener("input", () => this.autosize());
    this.input.addEventListener("keydown", (ev) => {
      // Enter sends; Shift+Enter is a new line. The box owns its keys.
      ev.stopPropagation();
      if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); this.submit(); }
    });
    this.sendBtn = el("button", "chat__send") as HTMLButtonElement;
    this.sendBtn.type = "button";
    this.sendBtn.addEventListener("click", () => {
      if (this.running && this.input.value.trim() === "") send({ type: "chat.stop", paneId: this.paneId });
      else this.submit();
    });
    composer.append(this.input, this.sendBtn);
    main.appendChild(composer);

    this.overview = new ChatOverview((text) => this.sendText(text));
    this.overview.onWaiting = (n) => {
      this.ovBtn.classList.toggle("chat__ovbtn--dot", n > 0);
      this.ovBtn.title = n > 0 ? `${n} thread${n === 1 ? "" : "s"} waiting on you` : "Show or hide the Overview";
    };

    const body = el("div", "chat__body");
    body.append(main, this.overview.element);
    this.element.appendChild(body);
    this.element.addEventListener("mousedown", () => send({ type: "pane.focus", paneId: this.paneId }));
    this.renderEmpty();
    this.updateButton();
  }

  attach(host: HTMLElement) {
    host.appendChild(this.element);
    this.autosize();
    if (!this.loaded) send({ type: "chat.request", paneId: this.paneId });
  }
  dispose() { this.overview.dispose(); this.element.remove(); }
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
    this.threads = threads;
    this.overview.setThreads(threads);
    for (const [id, cards] of this.threadCards) {
      const t = threads.find((x) => x.id === id);
      for (const c of cards) this.fillThreadCard(c, t);
    }
    this.refreshSuggestions();
  }

  // ---- host messages ------------------------------------------------------

  applyHistory(entries: ChatEntryView[], running: boolean, queued: number) {
    this.loaded = true;
    this.seen.clear();
    this.threadCards.clear();
    this.suggestCards.clear();
    this.log.replaceChildren();
    for (const e of entries) this.addEntry(e);
    if (!entries.length) this.renderEmpty();
    this.applyStatus(running, queued);
    this.refreshSuggestions();
    this.toBottom(true);
  }

  applyEntry(e: ChatEntryView) {
    if (!this.loaded) return;               // the history will carry it
    const stick = this.nearBottom();
    this.log.querySelector(".chat__welcome")?.remove();
    this.addEntry(e);
    this.refreshSuggestions();
    this.toBottom(stick || e.kind === "user");
  }

  applyStatus(running: boolean, queued: number) {
    this.running = running;
    this.busyEl.hidden = !running;
    this.busyEl.replaceChildren(el("span", "chat__spinner"), el("span", undefined, queued > 0 ? `Working · ${queued} more waiting` : "Working…"));
    this.stateEl.textContent = running ? "Working" : "";
    this.updateButton();
    if (running) this.toBottom(this.nearBottom());
  }

  applyMeta(meta: ChatMetaMessage) {
    this.meta = meta;
    this.overview.setMeta(meta);
    this.refreshSuggestions();
  }

  applyThreadTranscript(id: string, events: ThreadEventView[]) {
    this.overview.applyTranscript(id, events);
  }

  // ---- rendering ----------------------------------------------------------

  private addEntry(e: ChatEntryView) {
    if (this.seen.has(e.id)) return;
    this.seen.add(e.id);
    let row: HTMLElement;
    switch (e.kind) {
      case "user":
        row = el("div", "chat-row chat-row--user");
        row.appendChild(el("div", "chat-bubble", e.text));
        break;
      case "claude":
        row = el("div", "chat-row chat-row--claude md");
        row.appendChild(renderMarkdown(e.text));
        break;
      case "tool":
        row = el("div", "chat-row chat-row--tool");
        row.appendChild(el("span", "chat-tool__verb", TOOL_WORD[e.tool] ?? e.tool));
        if (e.text) row.appendChild(el("span", "chat-tool__target", e.text));
        break;
      case "thread":
        row = this.threadCard(e.tool.replace(/^thread:/, ""), e.text);
        break;
      case "suggest":
        row = this.suggestCard(e.tool.replace(/^suggest:/, ""), e.text);
        break;
      case "notice": {
        row = el("div", "chat-row chat-row--notice");
        row.appendChild(el("span", "chat-notice__mark", "Perch"));
        row.appendChild(el("span", "chat-notice__text", e.text));
        const threadId = e.tool.startsWith("thread:") ? e.tool.slice(7) : "";
        if (threadId) row.appendChild(this.openButton(threadId));
        break;
      }
      default:
        row = el("div", "chat-row chat-row--error", e.text);
    }
    // Consecutive tool rows read as one quiet block.
    const prev = this.log.lastElementChild;
    if (e.kind === "tool" && prev?.classList.contains("chat-row--tool")) row.classList.add("chat-row--tool-cont");
    this.log.appendChild(row);
  }

  private openButton(threadId: string): HTMLButtonElement {
    const b = el("button", "chat-notice__open", "Open thread") as HTMLButtonElement;
    b.type = "button";
    b.addEventListener("click", () => this.openThread(threadId));
    return b;
  }

  private openThread(id: string) {
    this.element.classList.remove("pane--chat-noov");
    this.ovBtn.setAttribute("aria-pressed", "true");
    this.overview.openThread(id);
  }

  /** A started thread, drawn as a live card under the turn that started it. */
  private threadCard(id: string, title: string): HTMLElement {
    const card = el("button", "chat-card");
    (card as HTMLButtonElement).type = "button";
    card.dataset.title = title;
    card.addEventListener("click", () => this.openThread(id));
    const list = this.threadCards.get(id) ?? [];
    list.push(card);
    this.threadCards.set(id, list);
    this.fillThreadCard(card, this.threads.find((t) => t.id === id));
    return card;
  }

  private fillThreadCard(card: HTMLElement, t: SessionView | undefined) {
    const top = el("div", "chat-card__top");
    top.append(el("span", "ovc__num", t ? `#${t.threadNumber}` : "#"), el("span", "chat-card__title", t?.title ?? card.dataset.title ?? ""), el("span", "ovc__dot"));
    const meta = el("div", "chat-card__meta", t ? stateLabel(t) : "Closed");
    card.dataset.group = t ? groupOf(t) : "resolved";
    card.replaceChildren(top, meta);
  }

  /** A proposed thread: its title and Start (or, once started, its number). */
  private suggestCard(id: string, title: string): HTMLElement {
    const card = el("div", "chat-card chat-card--suggest");
    card.dataset.id = id;
    card.dataset.title = title;
    this.suggestCards.set(id, card);
    this.fillSuggestCard(card);
    return card;
  }

  private fillSuggestCard(card: HTMLElement) {
    const id = card.dataset.id ?? "";
    const s = this.meta?.suggestions.find((x) => x.id === id);
    const started = s?.threadId ? this.threads.find((t) => t.id === s.threadId) : undefined;
    const top = el("div", "chat-card__top");
    top.append(el("span", "chat-card__tag", "Suggested"), el("span", "chat-card__title", card.dataset.title ?? ""));
    if (s?.threadId) {
      const open = el("button", "ovc__act", started ? `Started as #${started.threadNumber}` : "Started") as HTMLButtonElement;
      open.type = "button";
      open.addEventListener("click", () => s.threadId && this.openThread(s.threadId));
      top.appendChild(open);
    } else {
      const start = el("button", "ovc__act ovc__act--primary", "Start") as HTMLButtonElement;
      start.type = "button";
      start.addEventListener("click", () => {
        start.disabled = true;
        start.textContent = "Starting…";
        send({ type: "suggestion.start", sessionId: this.sessionId, id });
      });
      top.appendChild(start);
    }
    card.replaceChildren(top);
  }

  private refreshSuggestions() {
    for (const card of this.suggestCards.values()) this.fillSuggestCard(card);
    const waiting = (this.meta?.suggestions ?? []).filter((s) => !s.threadId);
    this.suggestBar.hidden = waiting.length < 2;
    if (waiting.length >= 2) {
      const b = el("button", "ovc__act ovc__act--primary", `Start all ${waiting.length} suggested threads`) as HTMLButtonElement;
      b.type = "button";
      b.addEventListener("click", () => {
        b.disabled = true;
        send({ type: "suggestion.start", sessionId: this.sessionId, id: "all" });
      });
      this.suggestBar.replaceChildren(b);
    }
  }

  private renderEmpty() {
    if (this.log.childElementCount) return;
    const w = el("div", "chat__welcome");
    w.appendChild(el("div", "chat__welcome-title", "Brief this chat like a chief of staff."));
    w.appendChild(el("p", undefined,
      "Say what you want done. It answers quick questions here, hands real work to threads — each a Claude in its own copy of the repo — and brings the results back. Tell it how you like to work: “propose threads before starting them”, “shorter updates”."));
    this.log.appendChild(w);
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
    this.sendBtn.textContent = stop ? "Stop" : "Send";
    this.sendBtn.classList.toggle("chat__send--stop", stop);
    this.input.placeholder = this.running
      ? "Claude is working — anything you send goes in next"
      : "Brief the chat — what should get done?";
  }

  private autosize() {
    // Empty, it is one line; with text, it grows to fit (up to a limit).
    this.input.style.height = "40px";
    if (this.input.value) this.input.style.height = `${Math.min(Math.max(this.input.scrollHeight, 40), 200)}px`;
    this.updateButton();
  }

  private nearBottom(): boolean {
    const s = this.scroll;
    return s.scrollHeight - s.scrollTop - s.clientHeight < 80;
  }

  private toBottom(force: boolean) {
    if (!force) return;
    requestAnimationFrame(() => { this.scroll.scrollTop = this.scroll.scrollHeight; });
  }
}
