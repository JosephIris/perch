// The Overview beside a project chat. Two views in one panel:
//
//   * the list: a greeting, then every thread grouped by what it needs from
//     you (Waiting on you · Ready for review · Working · Idle · Resolved),
//     one row each — title, what it is doing, its progress and its age;
//   * a thread opened in place: its task list, its conversation (with a
//     "New" mark where you left off), a box to steer it, and its branch with
//     Merge when its work is ready.
//
// Modelled on the Overview of Claude's own project chats. The chat stays in
// view while you look into a thread.
//
// State pushes arrive often and mostly change nothing, so nothing here is
// rebuilt wholesale: rows are kept per thread and updated in place, moved
// between groups with a short slide (FLIP), and the open thread's composer is
// never recreated — what you type and where the caret is survive every push.

import { send } from "./bridge.js";
import type { SessionView, ThreadEventView, ThreadTaskView, ChatMetaMessage } from "./bridge.js";
import { renderMarkdown } from "./md.js";
import {
  GROUPS, groupOf, statusLine, askText, lastActiveMs, byRecent, summarizeWork,
  el, button, icon, ring, setRing, ageLabel, setAge, reducedMotion, type ThreadGroup,
} from "./thread-ui.js";
import { elapsedSpan } from "./elapsed.js";

export { groupOf, stateLabel } from "./thread-ui.js";
export type { ThreadGroup } from "./thread-ui.js";

const EASE = "cubic-bezier(0, 0, 0, 1)";

// What you have seen, per thread, across restarts: the report time (the
// row's blue dot) and how many of its steps (the "New" mark). Per-viewer
// conveniences, so localStorage — and every access may throw.
function loadMap(key: string): Record<string, number> {
  try { return JSON.parse(localStorage.getItem(key) ?? "{}") ?? {}; } catch { return {}; }
}
function saveMap(key: string, map: Record<string, number>) {
  try { localStorage.setItem(key, JSON.stringify(map)); } catch { /* private window */ }
}
const SEEN_REPLY = "perch.projectchat.seenReply";
const SEEN_STEPS = "perch.projectchat.seenSteps";

type Row = {
  root: HTMLElement; title: HTMLElement; line: HTMLElement; tasks: HTMLElement; ringSvg: SVGSVGElement;
  tasksText: HTMLElement; merge: HTMLElement; mergeText: HTMLElement; age: HTMLElement; sig: string;
};

type Group = { section: HTMLElement; head: HTMLButtonElement; count: HTMLElement; inner: HTMLElement };

export class ChatOverview {
  readonly element: HTMLElement;
  private readonly bar: HTMLElement;
  private readonly views: HTMLElement;
  private readonly listView: HTMLElement;
  private readonly aboutView: HTMLElement;
  private readonly greetEl: HTMLElement;
  private readonly summaryEl: HTMLElement;
  private readonly emptyEl: HTMLElement;
  private readonly groups = new Map<ThreadGroup, Group>();
  private readonly rows = new Map<string, Row>();
  private readonly collapsed = new Set<ThreadGroup>(["resolved"]);
  private tab: "threads" | "about" = "threads";
  private threads: SessionView[] = [];
  private meta: ChatMetaMessage | null = null;
  private lastSig = "";
  private detail: ThreadDetail | null = null;
  private readonly seenReply = loadMap(SEEN_REPLY);
  private readonly seenSteps = loadMap(SEEN_STEPS);
  /** Where Merge sends its request: the chat's own composer. */
  private readonly ask: (text: string) => void;
  /** The number of threads waiting on you changed. */
  onWaiting: (n: number) => void = () => {};
  /** The ✕: hide the panel. */
  onClose: () => void = () => {};

  constructor(ask: (text: string) => void) {
    this.ask = ask;
    this.element = el("aside", "ov");
    this.bar = el("div", "ov__bar");
    this.views = el("div", "ov__views");

    this.listView = el("div", "ov__list");
    const greet = el("div", "ov__greet");
    this.greetEl = el("div", "ov__hello", "Welcome back.");
    this.summaryEl = el("div", "ov__summary");
    greet.append(this.greetEl, this.summaryEl);
    this.listView.appendChild(greet);
    this.emptyEl = el("p", "ov__hint",
      "When there's real work, the chat hands it to threads — each a Claude in its own copy of the repo. They show up here, grouped by what they need from you.");
    this.listView.appendChild(this.emptyEl);
    for (const g of GROUPS) this.listView.appendChild(this.makeGroup(g.id, g.label).section);

    this.aboutView = el("div", "ov__about");
    this.aboutView.hidden = true;
    this.views.append(this.listView, this.aboutView);
    this.element.append(this.bar, this.views);
    this.renderBar();
    this.renderList(false);
  }

  setThreads(threads: SessionView[]) {
    this.threads = threads;
    const sig = JSON.stringify(threads.map((t) => [t.id, t.title, t.agentState, t.dormant, t.branch, t.threadReply, t.threadReplyAtMs,
      t.threadResolved, t.threadUnmerged, t.threadTasksDone, t.threadTasksTotal, t.threadTaskNow, t.notification?.text, t.activityDetail,
      t.turnStartMs, t.doneAtMs, t.worktreeBranch]));
    if (sig === this.lastSig) return;
    this.lastSig = sig;
    this.onWaiting(threads.filter((t) => groupOf(t) === "waiting").length);
    this.renderList(true);
    this.detail?.update(this.threads.find((t) => t.id === this.detail?.id));
    if (this.detail) this.renderBar();
  }

  setMeta(meta: ChatMetaMessage) {
    this.meta = meta;
    this.renderGreeting();
    if (this.tab === "about") this.renderAbout();
  }

  applyTranscript(id: string, events: ThreadEventView[], tasks: ThreadTaskView[]) {
    if (this.detail?.id === id) this.detail.setTranscript(events, tasks);
  }

  /** Open a thread in the panel (from a row here or from the conversation). */
  openThread(id: string) {
    if (this.detail?.id === id) { this.detail.focus(); return; }
    this.leaveDetail(false);
    this.tab = "threads";
    this.aboutView.hidden = true;
    this.markReplySeen(id);
    this.detail = new ThreadDetail(id, this.seenSteps[id], (text) => this.ask(text), () => this.closeThread());
    this.detail.update(this.threads.find((t) => t.id === id));
    this.listView.hidden = true;
    this.views.appendChild(this.detail.element);
    this.element.classList.add("ov--detail");
    this.renderBar();
    this.enter(this.detail.element, 16);
    send({ type: "thread.transcript", id });
  }

  /** Open the About tab (the chat header's gear). */
  showAbout() {
    this.leaveDetail(false);
    this.selectTab("about");
  }

  dispose() { this.detail?.dispose(); }

  // ---- views ----------------------------------------------------------------

  private closeThread() {
    this.leaveDetail(true);
    this.renderBar();
  }

  private leaveDetail(animate: boolean) {
    if (!this.detail) return;
    this.seenSteps[this.detail.id] = this.detail.stepCount();
    saveMap(SEEN_STEPS, this.seenSteps);
    this.markReplySeen(this.detail.id);
    this.detail.dispose();
    this.detail.element.remove();
    this.detail = null;
    this.element.classList.remove("ov--detail");
    if (this.tab === "threads") {
      this.listView.hidden = false;
      this.renderList(false);
      if (animate) this.enter(this.listView, -16);
    }
  }

  private selectTab(tab: "threads" | "about") {
    this.tab = tab;
    this.listView.hidden = tab !== "threads";
    this.aboutView.hidden = tab !== "about";
    if (tab === "about") this.renderAbout();
    this.renderBar();
    this.enter(tab === "about" ? this.aboutView : this.listView, 0);
  }

  /** A view arriving: a short slide from the side it comes from, and a fade. */
  private enter(view: HTMLElement, dx: number) {
    if (reducedMotion()) return;
    view.animate([{ opacity: 0, transform: `translateX(${dx}px)` }, { opacity: 1, transform: "none" }], { duration: 200, easing: EASE });
  }

  private barSig = "";

  private renderBar() {
    const open = this.detail ? this.threads.find((x) => x.id === this.detail?.id) : undefined;
    // Rebuilt only when what it shows changed: a push must not drop a hover.
    const sig = this.detail ? `d|${this.detail.id}|${open?.title}|${open?.threadResolved}` : `l|${this.tab}`;
    if (sig === this.barSig) return;
    this.barSig = sig;
    this.bar.replaceChildren();
    if (this.detail) {
      const t = open;
      const crumb = el("div", "ov__crumb");
      crumb.append(
        button("ov__crumb-root", "Threads", () => this.closeThread()),
        icon("crumb", "pc-icon ov__crumb-sep"),
        el("span", "ov__crumb-title", t?.title ?? "Thread"));
      this.bar.appendChild(crumb);
      const tools = el("div", "ov__tools");
      if (t) {
        const resolve = button("ov__tool", icon("check"), () => send({ type: "thread.resolve", id: t.id, resolved: !t.threadResolved }),
          t.threadResolved ? "Reopen this thread" : "Resolve this thread");
        resolve.setAttribute("aria-pressed", String(!!t.threadResolved));
        tools.append(resolve, button("ov__tool", icon("expand"), () => send({ type: "session.select", id: t.id }), "Open its terminal"));
      }
      tools.appendChild(button("ov__tool", icon("close"), () => this.closeThread(), "Back to threads"));
      this.bar.appendChild(tools);
      return;
    }
    const tabs = el("div", "ov__tabs");
    for (const [id, label] of [["threads", "Threads"], ["about", "About"]] as const) {
      const b = button("ov__tab", label, () => { if (this.tab !== id) this.selectTab(id); });
      b.setAttribute("aria-selected", String(this.tab === id));
      tabs.appendChild(b);
    }
    this.bar.appendChild(tabs);
    const tools = el("div", "ov__tools");
    tools.appendChild(button("ov__tool", icon("close"), () => this.onClose(), "Hide the Overview"));
    this.bar.appendChild(tools);
  }

  private renderGreeting() {
    const name = this.meta?.userName?.trim();
    this.greetEl.textContent = name ? `Welcome back, ${name}.` : "Welcome back.";
    const waiting = this.threads.filter((t) => groupOf(t) === "waiting").length;
    this.summaryEl.textContent = !this.threads.length ? "No threads yet."
      : waiting === 0 ? "Nothing is waiting on you."
      : waiting === 1 ? "1 thread is waiting on you."
      : `${waiting} threads are waiting on you.`;
  }

  // ---- the list -------------------------------------------------------------

  private makeGroup(id: ThreadGroup, label: string): Group {
    const section = el("section", "ovg");
    section.dataset.group = id;
    const head = button("ovg__head", "", () => this.toggleGroup(id));
    const count = el("span", "ovg__count");
    head.append(icon("chevron", "pc-icon ovg__chev"), el("span", "ovg__label", label), count);
    const body = el("div", "ovg__body");
    const inner = el("div", "ovg__inner");
    body.appendChild(inner);
    section.append(head, body);
    const g = { section, head, count, inner };
    this.groups.set(id, g);
    this.applyCollapsed(id);
    return g;
  }

  private toggleGroup(id: ThreadGroup) {
    if (this.collapsed.has(id)) this.collapsed.delete(id); else this.collapsed.add(id);
    this.applyCollapsed(id);
  }

  private applyCollapsed(id: ThreadGroup) {
    const g = this.groups.get(id);
    if (!g) return;
    const shut = this.collapsed.has(id);
    g.section.classList.toggle("ovg--shut", shut);
    g.head.setAttribute("aria-expanded", String(!shut));
  }

  /** Put every thread's row in its group, newest activity first. With
   *  `animate`, rows and groups that moved slide from where they were. */
  private renderList(animate: boolean) {
    this.renderGreeting();
    const visible = animate && !this.listView.hidden && this.element.isConnected && !reducedMotion();
    const before = new Map<Element, number>();
    if (visible) {
      for (const r of this.rows.values()) if (r.root.isConnected) before.set(r.root, r.root.getBoundingClientRect().top);
      for (const g of this.groups.values()) if (!g.section.hidden) before.set(g.section, g.section.getBoundingClientRect().top);
    }

    const live = new Set(this.threads.map((t) => t.id));
    for (const [id, r] of this.rows) if (!live.has(id)) { r.root.remove(); this.rows.delete(id); }

    const byGroup = new Map<ThreadGroup, SessionView[]>();
    for (const t of this.threads) {
      const g = groupOf(t);
      byGroup.set(g, [...(byGroup.get(g) ?? []), t]);
    }
    for (const { id } of GROUPS) {
      const g = this.groups.get(id)!;
      const list = (byGroup.get(id) ?? []).sort(byRecent);
      g.section.hidden = list.length === 0;
      g.count.textContent = String(list.length);
      list.forEach((t, i) => {
        const row = this.rowFor(t);
        // Only move what is out of place: moving a hovered row would drop
        // its hover, and a no-op append still restarts nothing but costs.
        if (g.inner.children[i] !== row.root) g.inner.insertBefore(row.root, g.inner.children[i] ?? null);
      });
    }
    this.emptyEl.hidden = this.threads.length > 0;

    if (!visible) return;
    const moved = (node: Element, dy: number) =>
      (node as HTMLElement).animate([{ transform: `translateY(${dy}px)` }, { transform: "none" }], { duration: 220, easing: EASE });
    for (const r of this.rows.values()) {
      const was = before.get(r.root);
      if (was === undefined) {
        if (r.root.isConnected) r.root.animate([{ opacity: 0, transform: "translateY(4px)" }, { opacity: 1, transform: "none" }], { duration: 200, easing: EASE });
        continue;
      }
      const dy = was - r.root.getBoundingClientRect().top;
      if (Math.abs(dy) > 1) moved(r.root, dy);
    }
    for (const g of this.groups.values()) {
      if (g.section.hidden) continue;
      const was = before.get(g.section);
      if (was === undefined) { g.section.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 200, easing: EASE }); continue; }
      const dy = was - g.section.getBoundingClientRect().top;
      if (Math.abs(dy) > 1) moved(g.head, dy);
    }
  }

  private rowFor(t: SessionView): Row {
    let r = this.rows.get(t.id);
    if (!r) {
      const root = el("div", "ovr");
      root.tabIndex = 0;
      root.setAttribute("role", "button");
      root.addEventListener("click", () => this.openThread(t.id));
      root.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); this.openThread(t.id); } });
      const main = el("div", "ovr__main");
      const title = el("div", "ovr__title");
      const line = el("div", "ovr__line");
      main.append(title, line);
      const caps = el("div", "ovr__caps");
      const tasks = el("span", "pc-cap pc-cap--tasks");
      const ringSvg = ring();
      const tasksText = el("span");
      tasks.append(ringSvg, tasksText);
      const merge = el("span", "pc-cap pc-cap--merge");
      const mergeText = el("span");
      merge.append(icon("merge"), mergeText);
      caps.append(tasks, merge);
      const age = ageLabel(0, "pc-age ovr__age");
      root.append(el("span", "ovr__unread"), main, caps, age);
      r = { root, title, line, tasks, ringSvg, tasksText, merge, mergeText, age, sig: "" };
      this.rows.set(t.id, r);
    }
    this.fillRow(r, t);
    return r;
  }

  private fillRow(r: Row, t: SessionView) {
    const g = groupOf(t);
    const s = statusLine(t);
    const done = t.threadTasksDone ?? 0, total = t.threadTasksTotal ?? 0;
    const unread = (t.threadReplyAtMs ?? 0) > (this.seenFor(t));
    const sig = JSON.stringify([g, t.title, s, done, total, t.threadUnmerged, unread, lastActiveMs(t)]);
    if (sig === r.sig) return;
    r.sig = sig;
    r.root.dataset.group = g;
    r.root.classList.toggle("ovr--unread", unread);
    r.root.title = `#${t.threadNumber ?? ""} ${t.title}`;
    r.title.textContent = t.title;
    r.line.replaceChildren();
    if (s.blocked) r.line.append(el("span", "ovr__blocked", "Blocked"), " · ");
    r.line.append(s.text);
    // Progress while it has a task list and isn't done with; a bare spinner
    // while it works without one.
    const showTasks = g !== "resolved" && (total > 0 || g === "working");
    r.tasks.hidden = !showTasks;
    if (showTasks) {
      setRing(r.ringSvg, done, g === "working" && total === 0 ? 0 : total);
      r.tasksText.textContent = total > 0 ? `${done}/${total}` : "";
      r.tasks.classList.toggle("pc-cap--bare", total === 0);
    }
    const unmerged = t.threadUnmerged ?? 0;
    r.merge.hidden = unmerged <= 0 || g === "resolved";
    r.mergeText.textContent = String(unmerged);
    r.merge.title = `${unmerged} commit${unmerged === 1 ? "" : "s"} to merge`;
    setAge(r.age, lastActiveMs(t));
  }

  /** When you last saw a thread's report. A thread seen for the first time
   *  counts as read up to now, so opening a chat doesn't light every row. */
  private seenFor(t: SessionView): number {
    if (this.seenReply[t.id] === undefined) {
      this.seenReply[t.id] = t.threadReplyAtMs ?? 0;
      saveMap(SEEN_REPLY, this.seenReply);
    }
    return this.detail?.id === t.id ? Number.MAX_SAFE_INTEGER : this.seenReply[t.id];
  }

  private markReplySeen(id: string) {
    const t = this.threads.find((x) => x.id === id);
    this.seenReply[id] = Math.max(this.seenReply[id] ?? 0, t?.threadReplyAtMs ?? 0, Date.now());
    saveMap(SEEN_REPLY, this.seenReply);
  }

  // ---- About ----------------------------------------------------------------

  private renderAbout() {
    const m = this.meta;
    const wrap = this.aboutView;
    wrap.replaceChildren();
    const sessionId = m?.sessionId ?? "";
    const goal = document.createElement("input");
    goal.type = "text";
    goal.className = "settings-control settings-control--text ov__input";
    goal.value = m?.goal ?? "";
    goal.placeholder = "One line: what this chat is for";
    goal.setAttribute("aria-label", "Goal");
    const instructions = document.createElement("textarea");
    instructions.className = "settings-control settings-control--text settings-control--area ov__input";
    instructions.rows = 6;
    instructions.value = m?.instructions ?? "";
    instructions.placeholder = "Given to the chat and every new thread: branch to work on, how to check work, what needs your go-ahead…";
    instructions.setAttribute("aria-label", "Instructions");
    for (const c of [goal, instructions]) c.addEventListener("keydown", (e) => e.stopPropagation());
    const save = button("pc-btn pc-btn--primary", "Save", () =>
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
        button("pc-btn pc-btn--quiet", "Forget", () => send({ type: "projectchat.update", sessionId, forget: i + 1 })));
      list.appendChild(li);
    });
    wrap.appendChild(list);
  }
}

// ---- a thread opened in the panel ----------------------------------------------

class ThreadDetail {
  readonly id: string;
  readonly element: HTMLElement;
  private readonly scroll: HTMLElement;
  private readonly tasksEl: HTMLElement;
  private readonly log: HTMLElement;
  private readonly workingEl: HTMLElement;
  private readonly waitEl: HTMLElement;
  private readonly composer: HTMLElement;
  private readonly box: HTMLTextAreaElement;
  private readonly resolvedEl: HTMLElement;
  private readonly branchEl: HTMLElement;
  private thread: SessionView | undefined;
  private events: ThreadEventView[] | null = null;
  private tasks: ThreadTaskView[] = [];
  private eventsSig = "";
  private tasksSig = "";
  private waitSig = "";
  private workingSig = "";
  /** Steps seen when it was last open: the "New" mark goes after them. */
  private readonly seenSteps: number | undefined;
  /** A permission answered, so a push that still says "permission" doesn't
   *  offer the buttons again for the same prompt. */
  private answered: { text: string; at: number } | null = null;
  private poll = 0;
  private readonly ask: (text: string) => void;

  constructor(id: string, seenSteps: number | undefined, ask: (text: string) => void, private readonly back: () => void) {
    this.id = id;
    this.seenSteps = seenSteps;
    this.ask = ask;
    this.element = el("div", "ovd");
    this.scroll = el("div", "ovd__scroll");
    this.tasksEl = el("ul", "ovd__tasks");
    this.tasksEl.hidden = true;
    this.log = el("div", "ovd__log");
    this.log.appendChild(el("div", "ov__hint", "Loading its conversation…"));
    this.workingEl = el("div", "ovd__working");
    this.workingEl.hidden = true;
    this.scroll.append(this.tasksEl, this.log, this.workingEl);

    const foot = el("div", "ovd__foot");
    this.waitEl = el("div", "pc-card ovd__wait");
    this.waitEl.hidden = true;
    this.composer = el("div", "pc-composer pc-composer--thread");
    this.box = document.createElement("textarea");
    this.box.className = "pc-composer__input";
    this.box.rows = 1;
    this.box.placeholder = "Steer this thread…";
    this.box.setAttribute("aria-label", "Message this thread");
    this.box.addEventListener("input", () => this.autosize());
    this.box.addEventListener("keydown", (ev) => {
      ev.stopPropagation();
      if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); this.submit(); }
      if (ev.key === "Escape" && !this.box.value) { ev.preventDefault(); this.back(); }
    });
    this.composer.append(this.box, button("pc-composer__send", icon("enter"), () => this.submit(), "Send"));
    this.resolvedEl = el("div", "ovd__resolved");
    this.resolvedEl.hidden = true;
    this.branchEl = el("div", "ovd__branch");
    this.branchEl.hidden = true;
    foot.append(this.waitEl, this.composer, this.resolvedEl, this.branchEl);
    this.element.append(this.scroll, foot);

    // While it works, keep its conversation and task list current.
    this.poll = window.setInterval(() => {
      if (!this.element.isConnected) return;
      if (this.thread?.agentState === "working") send({ type: "thread.transcript", id: this.id });
    }, 2500);
  }

  dispose() { window.clearInterval(this.poll); }
  focus() { this.box.focus(); }
  stepCount(): number { return this.events?.length ?? this.seenSteps ?? 0; }

  update(t: SessionView | undefined) {
    const was = this.thread;
    this.thread = t;
    // It moved on (a turn started or ended, a report came in): fetch what it did.
    if (t && was && (was.agentState !== t.agentState || was.threadReplyAtMs !== t.threadReplyAtMs))
      send({ type: "thread.transcript", id: this.id });
    if (!t) {
      this.log.replaceChildren(el("div", "ov__hint", "This thread is closed."));
      this.composer.hidden = true;
      this.waitEl.hidden = true;
      this.workingEl.hidden = true;
      this.branchEl.hidden = true;
      this.resolvedEl.hidden = true;
      return;
    }
    const g = groupOf(t);
    this.renderWait(t, g);
    this.renderWorking(t);
    this.renderTasks();

    this.composer.hidden = !!t.threadResolved;
    this.resolvedEl.hidden = !t.threadResolved;
    if (t.threadResolved && !this.resolvedEl.childElementCount)
      this.resolvedEl.append(el("span", undefined, "This thread is resolved. Reopen it to send more messages."),
        button("pc-btn", "Reopen", () => send({ type: "thread.resolve", id: this.id, resolved: false })));

    const branch = t.worktreeBranch || t.branch;
    const unmerged = t.threadUnmerged ?? 0;
    this.branchEl.hidden = !branch;
    if (branch) {
      const sig = `${branch}|${unmerged}|${g}`;
      if (this.branchEl.dataset.sig !== sig) {
        this.branchEl.dataset.sig = sig;
        const name = el("span", "ovd__branch-name");
        name.append(icon("merge"), el("span", undefined, branch));
        this.branchEl.replaceChildren(name);
        if (unmerged > 0) {
          this.branchEl.appendChild(el("span", "ovd__branch-count", `${unmerged} commit${unmerged === 1 ? "" : "s"} to merge`));
          if (g !== "working" && g !== "resolved")
            this.branchEl.appendChild(button("pc-btn pc-btn--primary", "Merge", () =>
              this.ask(`Merge thread ${t.threadNumber} (${t.title}) — check its work first.`)));
        }
      }
    }
  }

  setTranscript(events: ThreadEventView[], tasks: ThreadTaskView[]) {
    this.tasks = tasks;
    this.renderTasks();
    const sig = `${events.length}|${events[events.length - 1]?.text ?? ""}|${events[events.length - 1]?.target ?? ""}`;
    if (sig === this.eventsSig) return;
    this.eventsSig = sig;
    const first = this.events === null;
    this.events = events;
    const stick = first || this.scroll.scrollHeight - this.scroll.scrollTop - this.scroll.clientHeight < 60;
    this.renderLog();
    if (stick) requestAnimationFrame(() => {
      // First open lands on the "New" mark when there is one, else the end.
      const mark = first ? this.log.querySelector<HTMLElement>(".pc-divider--new") : null;
      this.scroll.scrollTop = mark ? Math.max(0, mark.offsetTop - 24) : this.scroll.scrollHeight;
    });
  }

  private renderLog() {
    const events = this.events ?? [];
    this.log.replaceChildren();
    if (!events.length) { this.log.appendChild(el("div", "ov__hint", "Nothing yet — it's starting up.")); return; }
    const newAt = this.seenSteps !== undefined && this.seenSteps > 0 && this.seenSteps < events.length ? this.seenSteps : -1;
    let work: ThreadEventView[] = [];
    const flushWork = () => {
      if (!work.length) return;
      const line = summarizeWork(work);
      if (line) {
        const d = document.createElement("details");
        d.className = "pc-work";
        const sum = document.createElement("summary");
        sum.textContent = line;
        d.appendChild(sum);
        const list = el("div", "pc-work__list");
        for (const w of work) {
          if (/^(TaskCreate|TaskUpdate|TaskList|TaskGet|TodoWrite)$/.test(w.verb)) continue;
          const r = el("div", "pc-work__row");
          r.append(el("span", "pc-work__verb", w.verb || "Used"), el("span", "pc-work__target", w.target || w.text));
          list.appendChild(r);
        }
        d.appendChild(list);
        this.log.appendChild(d);
      }
      work = [];
    };
    events.forEach((e, i) => {
      if (i === newAt) { flushWork(); this.log.appendChild(divider("New", true)); }
      if (e.kind === "work") { work.push(e); return; }
      flushWork();
      this.log.appendChild(this.event(e));
    });
    flushWork();
  }

  private event(e: ThreadEventView): HTMLElement {
    if (e.kind === "prompt") {
      // Lines Perch typed carry a "[Perch #n]" tag; the brief's kick-off is
      // noise. What's left is who asked what.
      const text = e.text.replace(/^\[Perch #\d+\]\s*/, "");
      if (/^Start on the task in your brief\b/.test(text)) return el("div", "ovd__note", "Started on its brief");
      const fromChat = text.startsWith("From the project chat:");
      const row = el("div", "ovd__prompt");
      row.append(el("div", "pc-bubble", text.replace(/^From the project chat:\s*/, "")),
        el("div", "ovd__from", fromChat ? "From the project chat" : "From you"));
      return row;
    }
    const row = el("div", "ovd__beat md");
    row.appendChild(renderMarkdown(e.text));
    return row;
  }

  private renderTasks() {
    const t = this.thread;
    const waiting = t && groupOf(t) === "waiting" ? (t.agentState === "permission" ? askText(t) : (t.notification?.text || "an answer")) : "";
    const sig = JSON.stringify([this.tasks, waiting]);
    if (sig === this.tasksSig) return;
    this.tasksSig = sig;
    this.tasksEl.replaceChildren();
    for (const task of this.tasks) {
      const li = el("li", "ovd__task");
      li.dataset.status = task.status;
      const mark = el("span", "ovd__task-mark");
      if (task.status === "in_progress") { const r = ring(); setRing(r, 0, 0); mark.appendChild(r); }
      else if (task.status === "completed") mark.appendChild(icon("check"));
      li.append(mark, el("span", "ovd__task-text", task.status === "in_progress" && task.activeForm ? task.activeForm : task.subject));
      this.tasksEl.appendChild(li);
    }
    if (waiting) {
      const li = el("li", "ovd__task");
      li.dataset.status = "waiting";
      li.append(el("span", "ovd__task-mark"), el("span", "ovd__task-text", `Waiting on you: ${waiting}`));
      this.tasksEl.appendChild(li);
    }
    this.tasksEl.hidden = !this.tasksEl.childElementCount;
  }

  private renderWorking(t: SessionView) {
    const working = t.agentState === "working" && !t.dormant;
    const sig = working ? String(t.turnStartMs || 0) : "";
    if (sig === this.workingSig) return;
    this.workingSig = sig;
    this.workingEl.hidden = !working;
    if (!working) { this.workingEl.replaceChildren(); return; }
    const r = ring(); setRing(r, 0, 0);
    const text = el("span", "ovd__working-text", "Claude is working");
    if (t.turnStartMs) text.append(" · ", elapsedSpan(t.turnStartMs));
    this.workingEl.replaceChildren(r, text, button("pc-btn pc-btn--quiet", "Stop", () => send({ type: "thread.stop", id: this.id })));
  }

  /** What it needs from you, above the composer: a permission to answer
   *  here, or a question to answer in the box below. */
  private renderWait(t: SessionView, g: ThreadGroup) {
    const permission = g === "waiting" && t.agentState === "permission";
    const text = permission ? askText(t) : (t.notification?.text || "");
    if (this.answered && (!permission || Date.now() - this.answered.at > 4000 || this.answered.text !== text)) this.answered = null;
    const sig = g === "waiting" ? `${t.agentState}|${text}|${this.answered ? 1 : 0}` : "";
    if (sig === this.waitSig) return;
    const appearing = this.waitEl.hidden && g === "waiting";
    this.waitSig = sig;
    this.waitEl.hidden = g !== "waiting";
    if (g !== "waiting") { this.waitEl.replaceChildren(); return; }
    const head = el("div", "pc-card__head");
    head.append(icon("hand", "pc-icon pc-card__hand"),
      el("span", "pc-card__title", permission ? "It needs your permission" : "It's waiting for your answer"));
    const body = el("div", "pc-card__line", permission ? `It asks to: ${text}` : (text || "Reply in the box below."));
    this.waitEl.replaceChildren(head, body);
    if (permission) {
      const acts = el("div", "pc-card__actions");
      const answer = (reply: "allow" | "deny") => {
        this.answered = { text, at: Date.now() };
        for (const b of acts.querySelectorAll("button")) (b as HTMLButtonElement).disabled = true;
        send({ type: "thread.answer", id: this.id, text: reply });
      };
      const allow = button("pc-btn pc-btn--primary", "Allow", () => answer("allow"));
      const deny = button("pc-btn", "Deny", () => answer("deny"));
      if (this.answered) { allow.disabled = true; deny.disabled = true; }
      acts.append(allow, deny, button("pc-btn pc-btn--quiet", "See it in its terminal", () => send({ type: "session.select", id: this.id })));
      this.waitEl.appendChild(acts);
    }
    if (appearing && !reducedMotion())
      this.waitEl.animate([{ opacity: 0, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }], { duration: 200, easing: EASE });
  }

  private submit() {
    const text = this.box.value.trim();
    if (!text) return;
    send({ type: "thread.send", id: this.id, text });
    this.box.value = "";
    this.autosize();
  }

  private autosize() {
    this.box.style.height = "auto";
    this.box.style.height = `${Math.min(Math.max(this.box.scrollHeight, 24), 160)}px`;
  }
}

/** A wavy rule with a word in the middle: a day in the conversation, or
 *  "New" where a thread's unseen steps begin. */
export function divider(label: string, isNew = false): HTMLElement {
  const d = el("div", isNew ? "pc-divider pc-divider--new" : "pc-divider");
  d.setAttribute("role", "separator");
  d.append(el("span", "pc-divider__wave"), el("span", "pc-divider__label", label), el("span", "pc-divider__wave"));
  return d;
}
