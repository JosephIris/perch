// The Overview beside a project chat: every thread grouped by what it needs
// from you, a thread opened in place (its conversation, a box to message it,
// Stop, Merge, Resolve), and an About tab for the chat's goal, instructions
// and memory. Modelled on the Overview pane of Claude's own project chats:
// the chat stays in view while you look into a thread.

import { send } from "./bridge.js";
import type { SessionView, AgentStateName, ThreadEventView, ChatMetaMessage } from "./bridge.js";
import { renderMarkdown } from "./md.js";
import { agoSpan } from "./elapsed.js";

export type ThreadGroup = "waiting" | "working" | "ready" | "idle" | "resolved";

const GROUPS: { id: ThreadGroup; label: string }[] = [
  { id: "waiting", label: "Waiting on you" },
  { id: "working", label: "Working" },
  { id: "ready", label: "Ready to merge" },
  { id: "idle", label: "Idle" },
  { id: "resolved", label: "Resolved" },
];

const STATE_WORD: Record<AgentStateName, string> = {
  working: "Working", waiting: "Waiting for you", permission: "Needs your permission", done: "Finished its turn", idle: "Idle",
};

/** Which group a thread belongs in. Pure; the order of the checks is the
 *  order of what matters: done with → resolved; blocked on you → waiting. */
export function groupOf(t: SessionView): ThreadGroup {
  if (t.threadResolved) return "resolved";
  if (!t.dormant && (t.agentState === "permission" || t.agentState === "waiting")) return "waiting";
  if (!t.dormant && t.agentState === "working") return "working";
  if ((t.threadUnmerged ?? 0) > 0) return "ready";
  return "idle";
}

export function stateLabel(t: SessionView): string {
  if (t.threadResolved) return "Resolved";
  if (t.dormant) return "Asleep";
  const g = groupOf(t);
  if (g === "ready") return `${t.threadUnmerged} commit${t.threadUnmerged === 1 ? "" : "s"} to merge`;
  return STATE_WORD[t.agentState];
}

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

function button(cls: string, text: string, onClick: () => void): HTMLButtonElement {
  const b = el("button", cls, text) as HTMLButtonElement;
  b.type = "button";
  b.addEventListener("click", (e) => { e.stopPropagation(); onClick(); });
  return b;
}

export class ChatOverview {
  readonly element: HTMLElement;
  private readonly tabsEl: HTMLElement;
  private readonly body: HTMLElement;
  private tab: "threads" | "about" = "threads";
  private threads: SessionView[] = [];
  private meta: ChatMetaMessage | null = null;
  private openId: string | null = null;
  private readonly transcript = new Map<string, ThreadEventView[]>();
  private poll = 0;
  private lastSig = "";
  /** What you're typing to the open thread. The panel redraws as the thread
   *  moves on; the draft and the caret must survive that. */
  private draft = "";
  private draftFocused = false;
  /** Where Merge sends its request: the chat's own composer. */
  private readonly ask: (text: string) => void;
  /** Called when the number of threads waiting on you changes. */
  onWaiting: (n: number) => void = () => {};

  constructor(ask: (text: string) => void) {
    this.ask = ask;
    this.element = el("aside", "ov");
    this.tabsEl = el("div", "ov__tabs");
    this.body = el("div", "ov__body");
    this.element.append(this.tabsEl, this.body);
    this.renderTabs();
    this.render();
  }

  setThreads(threads: SessionView[]) {
    this.threads = threads;
    const sig = JSON.stringify(threads.map((t) => [t.id, t.title, t.agentState, t.dormant, t.branch, t.threadReply, t.threadReplyAtMs, t.threadResolved, t.threadUnmerged]));
    if (sig === this.lastSig) return;
    this.lastSig = sig;
    this.onWaiting(threads.filter((t) => groupOf(t) === "waiting").length);
    // An open thread that moved on: fetch what it said.
    if (this.openId) send({ type: "thread.transcript", id: this.openId });
    this.render();
  }

  setMeta(meta: ChatMetaMessage) {
    this.meta = meta;
    if (this.tab === "about") this.render();
  }

  applyTranscript(id: string, events: ThreadEventView[]) {
    this.transcript.set(id, events);
    if (id === this.openId) this.render(true);
  }

  /** Open a thread in the panel (from a card here or in the conversation). */
  openThread(id: string) {
    this.tab = "threads";
    this.openId = id;
    this.renderTabs();
    send({ type: "thread.transcript", id });
    window.clearInterval(this.poll);
    // While it's working, keep its conversation current.
    this.poll = window.setInterval(() => {
      const t = this.threads.find((x) => x.id === this.openId);
      if (!t || !this.element.isConnected) { window.clearInterval(this.poll); return; }
      if (t.agentState === "working") send({ type: "thread.transcript", id: t.id });
    }, 3000);
    this.render();
    this.element.classList.add("ov--detail");
  }

  private closeThread() {
    this.openId = null;
    this.draft = "";
    this.draftFocused = false;
    window.clearInterval(this.poll);
    this.element.classList.remove("ov--detail");
    this.render();
  }

  dispose() { window.clearInterval(this.poll); }

  // ---- rendering ----------------------------------------------------------

  private renderTabs() {
    this.tabsEl.replaceChildren();
    for (const [id, label] of [["threads", "Threads"], ["about", "About"]] as const) {
      const b = button("ov__tab", label, () => {
        this.tab = id;
        if (id === "about") this.closeThread();
        this.renderTabs();
        this.render();
      });
      b.setAttribute("aria-selected", String(this.tab === id));
      this.tabsEl.appendChild(b);
    }
  }

  private render(keepScroll = false) {
    const prev = this.body.querySelector(".ovt__log") as HTMLElement | null;
    const stick = prev ? prev.scrollHeight - prev.scrollTop - prev.clientHeight < 60 : true;
    const top = prev?.scrollTop ?? 0;
    this.body.replaceChildren(this.tab === "about" ? this.about() : this.openId ? this.detail() : this.list());
    const log = this.body.querySelector(".ovt__log") as HTMLElement | null;
    if (log) requestAnimationFrame(() => { log.scrollTop = keepScroll && !stick ? top : log.scrollHeight; });
  }

  private list(): HTMLElement {
    const wrap = el("div", "ov__list");
    if (!this.threads.length) {
      const empty = el("div", "ov__empty");
      empty.appendChild(el("p", undefined, "No threads yet."));
      empty.appendChild(el("p", "ov__hint",
        "When there's real work, the chat hands it to threads — each a Claude in its own copy of the repo. They show up here, grouped by what they need from you."));
      wrap.appendChild(empty);
      return wrap;
    }
    const sorted = [...this.threads].sort((a, b) => (b.threadNumber ?? 0) - (a.threadNumber ?? 0));
    for (const g of GROUPS) {
      const inGroup = sorted.filter((t) => groupOf(t) === g.id);
      if (!inGroup.length) continue;
      const section = el("section", `ov__group ov__group--${g.id}`);
      const head = el("div", "ov__group-head");
      head.append(el("span", undefined, g.label), el("span", "ov__group-count", String(inGroup.length)));
      section.appendChild(head);
      if (g.id === "resolved") {
        // Done with: folded away, like a mail client's archive.
        const det = document.createElement("details");
        det.className = "ov__resolved";
        const sum = document.createElement("summary");
        sum.textContent = `Show ${inGroup.length} resolved`;
        det.appendChild(sum);
        for (const t of inGroup) det.appendChild(this.card(t));
        section.appendChild(det);
      } else {
        for (const t of inGroup) section.appendChild(this.card(t));
      }
      wrap.appendChild(section);
    }
    return wrap;
  }

  private card(t: SessionView): HTMLElement {
    const g = groupOf(t);
    const card = el("div", "ovc");
    card.dataset.group = g;
    card.tabIndex = 0;
    card.addEventListener("click", () => this.openThread(t.id));
    card.addEventListener("keydown", (e) => { if (e.key === "Enter") this.openThread(t.id); });

    const top = el("div", "ovc__top");
    top.append(el("span", "ovc__num", `#${t.threadNumber ?? ""}`), el("span", "ovc__title", t.title), el("span", "ovc__dot"));
    card.appendChild(top);
    const meta = el("div", "ovc__meta");
    meta.appendChild(el("span", "ovc__state", stateLabel(t)));
    if (t.branch) meta.appendChild(el("span", "ovc__branch", `⎇ ${t.branch}`));
    if (t.threadReplyAtMs) { const w = el("span", "ovc__when"); w.appendChild(agoSpan(t.threadReplyAtMs)); meta.appendChild(w); }
    card.appendChild(meta);
    if (t.threadReply && g !== "resolved")
      card.appendChild(el("div", "ovc__reply", t.threadReply.replace(/\*\*|__|`/g, "")));

    const actions = el("div", "ovc__actions");
    if (g === "waiting") actions.appendChild(button("ovc__act ovc__act--primary", "Answer", () => send({ type: "session.select", id: t.id })));
    if (g === "ready") actions.appendChild(button("ovc__act ovc__act--primary", "Merge", () => this.merge(t)));
    actions.appendChild(button("ovc__act", t.threadResolved ? "Reopen" : "Resolve",
      () => send({ type: "thread.resolve", id: t.id, resolved: !t.threadResolved })));
    card.appendChild(actions);
    return card;
  }

  private merge(t: SessionView) {
    this.ask(`Merge thread ${t.threadNumber} (${t.title}) — check its work first.`);
  }

  private detail(): HTMLElement {
    const t = this.threads.find((x) => x.id === this.openId);
    const wrap = el("div", "ovt");
    const head = el("div", "ovt__head");
    head.appendChild(button("ovt__back", "← Threads", () => this.closeThread()));
    if (!t) {
      wrap.append(head, el("div", "ov__empty", "This thread is closed."));
      return wrap;
    }
    const title = el("div", "ovt__title");
    title.append(el("span", "ovc__num", `#${t.threadNumber ?? ""}`), el("span", undefined, t.title));
    head.appendChild(title);
    const state = el("div", "ovt__state");
    state.dataset.group = groupOf(t);
    state.append(el("span", "ovc__dot"), el("span", undefined, stateLabel(t)));
    if (t.branch) state.appendChild(el("span", "ovc__branch", `⎇ ${t.branch}`));
    head.appendChild(state);
    const acts = el("div", "ovt__actions");
    if (t.agentState === "working") acts.appendChild(button("ovc__act", "Stop", () => send({ type: "thread.stop", id: t.id })));
    if (groupOf(t) === "ready") acts.appendChild(button("ovc__act ovc__act--primary", "Merge", () => this.merge(t)));
    acts.appendChild(button("ovc__act", t.threadResolved ? "Reopen" : "Resolve",
      () => send({ type: "thread.resolve", id: t.id, resolved: !t.threadResolved })));
    acts.appendChild(button("ovc__act", "Open terminal", () => send({ type: "session.select", id: t.id })));
    head.appendChild(acts);
    wrap.appendChild(head);

    if (groupOf(t) === "waiting") {
      const banner = el("div", "ovt__banner");
      const what = t.agentState === "permission"
        ? (t.notification?.text || t.activityDetail || "It's asking for your permission.")
        : "It's waiting for you.";
      banner.appendChild(el("span", undefined, what));
      if (t.agentState === "permission") {
        // One answer: both buttons go quiet once either is pressed; the next
        // prompt (if any) draws a fresh banner.
        const answer = (text: "allow" | "deny") => {
          for (const b of banner.querySelectorAll("button")) (b as HTMLButtonElement).disabled = true;
          send({ type: "thread.answer", id: t.id, text });
        };
        banner.appendChild(button("ovc__act ovc__act--primary", "Allow", () => answer("allow")));
        banner.appendChild(button("ovc__act", "Deny", () => answer("deny")));
      }
      banner.appendChild(button("ovc__act", "See it", () => send({ type: "session.select", id: t.id })));
      wrap.appendChild(banner);
    }

    const log = el("div", "ovt__log");
    const events = this.transcript.get(t.id);
    if (!events) log.appendChild(el("div", "ov__hint", "Loading its conversation…"));
    else if (!events.length) log.appendChild(el("div", "ov__hint", "Nothing yet — it's starting up."));
    for (const e of events ?? []) log.appendChild(this.event(e));
    wrap.appendChild(log);

    const composer = el("div", "ovt__composer");
    const box = document.createElement("textarea");
    box.className = "chat__input";
    box.rows = 1;
    box.placeholder = `Message thread ${t.threadNumber} directly`;
    box.value = this.draft;
    box.addEventListener("input", () => { this.draft = box.value; });
    box.addEventListener("focus", () => { this.draftFocused = true; });
    box.addEventListener("blur", () => { this.draftFocused = false; });
    if (this.draftFocused) requestAnimationFrame(() => { box.focus(); box.setSelectionRange(box.value.length, box.value.length); });
    const submit = () => {
      const text = box.value.trim();
      if (!text) return;
      send({ type: "thread.send", id: t.id, text });
      box.value = "";
      this.draft = "";
    };
    box.addEventListener("keydown", (ev) => {
      ev.stopPropagation();
      if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); submit(); }
    });
    composer.append(box, button("chat__send", "Send", submit));
    wrap.appendChild(composer);
    return wrap;
  }

  private event(e: ThreadEventView): HTMLElement {
    if (e.kind === "prompt") {
      // Lines Perch typed carry a "[Perch #n]" tag; the brief's kick-off is
      // noise. What's left is who asked what.
      const text = e.text.replace(/^\[Perch #\d+\]\s*/, "");
      if (/^Start on the task in your brief\.?$/.test(text)) return el("div", "ovt__note", "Started on its brief");
      const from = text.startsWith("From the project chat:") ? "Project chat" : "You";
      const row = el("div", "ovt__prompt");
      row.append(el("span", "ovt__from", from), el("div", "ovt__bubble", text.replace(/^From the project chat:\s*/, "")));
      return row;
    }
    if (e.kind === "beat") {
      const row = el("div", "ovt__beat md");
      row.appendChild(renderMarkdown(e.text));
      return row;
    }
    const row = el("div", "chat-row chat-row--tool");
    row.append(el("span", "chat-tool__verb", e.verb || "Used"), el("span", "chat-tool__target", e.target || e.text));
    return row;
  }

  private about(): HTMLElement {
    const m = this.meta;
    const wrap = el("div", "ov__about");
    const sessionId = m?.sessionId ?? "";
    const goal = document.createElement("input");
    goal.type = "text";
    goal.className = "settings-control settings-control--text ov__input";
    goal.value = m?.goal ?? "";
    goal.placeholder = "One line: what this chat is for";
    const instructions = document.createElement("textarea");
    instructions.className = "settings-control settings-control--text settings-control--area ov__input";
    instructions.rows = 6;
    instructions.value = m?.instructions ?? "";
    instructions.placeholder = "Given to the chat and every new thread: branch to work on, how to check work, what needs your go-ahead…";
    for (const c of [goal, instructions]) c.addEventListener("keydown", (e) => e.stopPropagation());
    const save = button("ovc__act ovc__act--primary", "Save", () =>
      send({ type: "projectchat.update", sessionId, goal: goal.value, instructions: instructions.value }));

    wrap.append(el("div", "ov__label", "Goal"), goal, el("div", "ov__label", "Instructions"), instructions);
    const row = el("div", "ov__save");
    row.append(el("span", "ov__hint", "New threads get the change; running ones keep what they started with."), save);
    wrap.appendChild(row);

    wrap.appendChild(el("div", "ov__label", "Memory"));
    const mem = m?.memory ?? [];
    if (!mem.length) wrap.appendChild(el("p", "ov__hint",
      "Nothing yet. Tell the chat to remember a decision, a requirement or how you like to work, and it goes here — every new thread reads it."));
    const list = el("ol", "ov__memory");
    mem.forEach((note, i) => {
      const li = el("li", "ov__note");
      li.append(el("span", undefined, note),
        button("ovc__act", "Forget", () => send({ type: "projectchat.update", sessionId, forget: i + 1 })));
      list.appendChild(li);
    });
    wrap.appendChild(list);
    return wrap;
  }
}
