// ChatPane = a project chat: the conversation with its coordinator Claude on
// the left, its threads on the right. Deliberately not a terminal — it is the
// one place in Perch you talk to Claude as a conversation, so it looks like
// one.
//
// The host owns the conversation (ChatController): the page asks for the
// history once, then receives each row as it is written (chat.entry) and
// whether a turn is running (chat.status). Rows are the user's messages,
// Claude's prose (Markdown), one compact line per tool Claude used, Perch's
// notices (a thread finished, a thread asks) and errors.

import { send } from "./bridge.js";
import type { PaneTreeView, SessionView, ChatEntryView } from "./bridge.js";
import { renderMarkdown } from "./md.js";
import { ThreadsView } from "./threads-pane.js";

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

/** A few words for a tool row's verb. */
const TOOL_WORD: Record<string, string> = {
  Read: "Read", Grep: "Searched", Glob: "Listed", LS: "Listed", Bash: "Ran",
  WebSearch: "Searched the web", WebFetch: "Fetched", TodoWrite: "Updated its plan",
};

export class ChatPane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly titleEl: HTMLElement;
  private readonly stateEl: HTMLElement;
  private readonly scroll: HTMLElement;
  private readonly log: HTMLElement;
  private readonly busyEl: HTMLElement;
  private readonly input: HTMLTextAreaElement;
  private readonly sendBtn: HTMLButtonElement;
  private readonly threads = new ThreadsView();
  private running = false;
  private loaded = false;
  private readonly seen = new Set<string>();

  constructor(paneId: string) {
    this.paneId = paneId;
    this.element = el("div", "pane pane--chat");
    this.element.dataset.paneId = paneId;

    const main = el("section", "chat");
    const head = el("header", "chat__head");
    this.titleEl = el("div", "chat__title", "Project chat");
    this.stateEl = el("div", "chat__state");
    head.append(this.titleEl, this.stateEl);
    main.appendChild(head);

    this.scroll = el("div", "chat__scroll");
    this.log = el("div", "chat__log");
    this.busyEl = el("div", "chat__busy");
    this.busyEl.hidden = true;
    this.scroll.append(this.log, this.busyEl);
    main.appendChild(this.scroll);

    const composer = el("div", "chat__composer");
    this.input = document.createElement("textarea");
    this.input.className = "chat__input";
    this.input.rows = 1;
    this.input.placeholder = "Brief the chat — what should get done?";
    this.input.setAttribute("aria-label", "Message the project chat");
    this.input.addEventListener("input", () => this.autosize());
    this.input.addEventListener("keydown", (ev) => {
      // Enter sends; Shift+Enter is a new line. Nothing else here is a chord
      // for the rest of the app — the box owns its keys.
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

    const side = el("aside", "chat__side");
    side.appendChild(this.threads.element);

    const body = el("div", "chat__body");
    body.append(main, side);
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
  dispose() { this.element.remove(); }
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

  /** The tab this chat is (for its title) and its threads. */
  setSession(session: SessionView | undefined, threads: SessionView[]) {
    if (session) this.titleEl.textContent = session.title;
    this.threads.setThreads(threads);
  }

  // ---- host messages ------------------------------------------------------

  applyHistory(entries: ChatEntryView[], running: boolean, queued: number) {
    this.loaded = true;
    this.seen.clear();
    this.log.replaceChildren();
    for (const e of entries) this.addEntry(e);
    if (!entries.length) this.renderEmpty();
    this.applyStatus(running, queued);
    this.toBottom(true);
  }

  applyEntry(e: ChatEntryView) {
    if (!this.loaded) return;               // the history will carry it
    const stick = this.nearBottom();
    this.log.querySelector(".chat__welcome")?.remove();
    this.addEntry(e);
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
      case "tool": {
        row = el("div", "chat-row chat-row--tool");
        row.appendChild(el("span", "chat-tool__verb", TOOL_WORD[e.tool] ?? e.tool));
        if (e.text) row.appendChild(el("span", "chat-tool__target", e.text));
        break;
      }
      case "notice": {
        row = el("div", "chat-row chat-row--notice");
        row.appendChild(el("span", "chat-notice__mark", "Perch"));
        row.appendChild(el("span", "chat-notice__text", e.text));
        // A notice about one thread (it's waiting on you) takes you there.
        const threadId = e.tool.startsWith("thread:") ? e.tool.slice(7) : "";
        if (threadId) {
          const open = el("button", "chat-notice__open", "Open thread") as HTMLButtonElement;
          open.type = "button";
          open.addEventListener("click", () => send({ type: "session.select", id: threadId }));
          row.appendChild(open);
        }
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

  private renderEmpty() {
    if (this.log.childElementCount) return;
    const w = el("div", "chat__welcome");
    w.appendChild(el("div", "chat__welcome-title", "Brief this chat like a chief of staff."));
    w.appendChild(el("p", undefined,
      "Say what you want done. It reads the project, splits real work into threads — each a Claude in its own copy of the repo — and brings the results back here."));
    this.log.appendChild(w);
  }

  private submit() {
    const text = this.input.value.trim();
    if (!text) return;
    send({ type: "chat.send", paneId: this.paneId, text });
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
