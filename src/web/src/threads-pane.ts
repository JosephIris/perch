// The threads of a project chat, as cards: state, branch, the head of each
// thread's last report. A card opens that thread's tab.
//
// ThreadsView is the list itself, used beside a project chat's conversation
// (chat-pane.ts). ThreadsPane wraps it as a pane of its own — the right half
// of the earlier, terminal-based project chat.
//
// No data of its own: threads are ordinary sessions carrying `threadOf`, and
// the workspace hands over the ones belonging to a chat on every state push,
// so the cards can never disagree with the sidebar.

import { send } from "./bridge.js";
import type { PaneTreeView, SessionView, AgentStateName } from "./bridge.js";
import { buildPaneHeader, applyChips } from "./pane-header.js";
import { agoSpan } from "./elapsed.js";

const STATE_WORD: Record<AgentStateName, string> = {
  working: "Working",
  waiting: "Waiting for you",
  permission: "Needs permission",
  done: "Finished its turn",
  idle: "Idle",
};

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

export class ThreadsView {
  readonly element: HTMLElement;
  /** Last rendered content, so a state push that changed nothing about the
   *  threads doesn't rebuild the cards (and lose hover / scroll). */
  private signature: string | null = null;

  constructor() {
    this.element = el("div", "threads");
    this.setThreads([]);
  }

  /** This project chat's threads, lowest number first. */
  setThreads(threads: SessionView[]) {
    const sorted = [...threads].sort((a, b) => (a.threadNumber ?? 0) - (b.threadNumber ?? 0));
    const sig = JSON.stringify(sorted.map((t) => [t.id, t.title, t.agentState, t.dormant, t.branch, t.threadReply, t.threadReplyAtMs]));
    if (sig === this.signature) return;
    this.signature = sig;

    const frag = document.createDocumentFragment();
    const head = el("div", "threads__head");
    head.appendChild(el("div", "threads__title", "Threads"));
    if (sorted.length) {
      const working = sorted.filter((t) => t.agentState === "working").length;
      head.appendChild(el("div", "threads__count", working ? `${working} of ${sorted.length} working` : `${sorted.length}`));
    }
    frag.appendChild(head);

    if (!sorted.length) {
      const empty = el("div", "threads__empty");
      empty.appendChild(el("p", undefined, "No threads yet."));
      empty.appendChild(el("p", "threads__hint",
        "When there's real work to do, the chat hands it to threads: each one a Claude in its own copy of the repo. They show up here."));
      frag.appendChild(empty);
    } else {
      const list = el("div", "threads__list");
      for (const t of sorted) list.appendChild(this.card(t));
      frag.appendChild(list);
    }
    this.element.replaceChildren(frag);
  }

  private card(t: SessionView): HTMLElement {
    const state: AgentStateName = t.dormant ? "idle" : t.agentState;
    const card = el("button", "thread-card") as HTMLButtonElement;
    card.type = "button";
    card.dataset.state = state;
    card.title = "Open this thread";
    card.addEventListener("click", () => send({ type: "session.select", id: t.id }));

    const top = el("div", "thread-card__top");
    top.appendChild(el("span", "thread-card__num", `#${t.threadNumber ?? ""}`));
    top.appendChild(el("span", "thread-card__title", t.title));
    top.appendChild(el("span", "thread-card__dot"));
    card.appendChild(top);

    const meta = el("div", "thread-card__meta");
    meta.appendChild(el("span", "thread-card__state", t.dormant ? "Asleep" : STATE_WORD[state]));
    if (t.branch) meta.appendChild(el("span", "thread-card__branch", `⎇ ${t.branch}`));
    if (t.threadReplyAtMs) {
      const when = el("span", "thread-card__when");
      when.append("reported ");
      when.appendChild(agoSpan(t.threadReplyAtMs));
      meta.appendChild(when);
    }
    card.appendChild(meta);

    // The report is Markdown; a card is a glance, so drop the emphasis marks.
    if (t.threadReply) card.appendChild(el("div", "thread-card__reply", t.threadReply.replace(/\*\*|__|`/g, "")));
    return card;
  }
}

export class ThreadsPane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly nameEl: HTMLElement;
  private readonly stateDotEl: HTMLElement;
  private readonly colorDotEl: HTMLElement;
  private readonly branchEl: HTMLElement;
  private readonly commitsEl: HTMLElement;
  private readonly view = new ThreadsView();

  constructor(paneId: string, name: string) {
    this.paneId = paneId;
    this.element = el("div", "pane pane--threads");
    this.element.dataset.paneId = paneId;

    const header = buildPaneHeader(paneId);
    this.element.appendChild(header.root);
    this.nameEl = header.nameEl;
    this.stateDotEl = header.stateDotEl;
    this.colorDotEl = header.colorDotEl;
    this.branchEl = header.branchEl;
    this.commitsEl = header.commitsEl;
    this.nameEl.textContent = name;

    const slot = el("div", "pane__threadslot");
    slot.appendChild(this.view.element);
    this.element.appendChild(slot);
    this.element.addEventListener("mousedown", () => send({ type: "pane.focus", paneId: this.paneId }));
  }

  attach(host: HTMLElement) { host.appendChild(this.element); }
  dispose() { this.element.remove(); }
  setName(name: string) { this.nameEl.textContent = name; }
  setActive(active: boolean) { this.element.classList.toggle("pane--active", active); }
  focus() { /* nothing to type into */ }
  feed(_b64: string) { /* no terminal */ }
  notifyExit(_code: number) { /* nothing to exit */ }
  forceRefit() { /* flows like a document */ }
  changeFontSize(): number { return 0; }
  resetFontSize(): number { return 0; }

  applyLeafView(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    this.nameEl.textContent = leaf.name;
    this.stateDotEl.dataset.state = leaf.agentState;
    this.colorDotEl.dataset.color = String(leaf.colorIndex);
    this.element.dataset.color = String(leaf.colorIndex);
    applyChips(this.branchEl, this.commitsEl, leaf, false);
  }

  setThreads(threads: SessionView[]) { this.view.setThreads(threads); }
}
