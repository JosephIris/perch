// The Team room: one conversation across a project's bots, with you in it.
//
// A full-height surface laid over the workspace + inspector columns, NOT a
// pane leaf and NOT a fixed-inset page. A leaf lives in one session's tree
// (closing that tab would kill the room); a fixed page would hide the sidebar,
// and the sidebar's bot rows are the fastest way to jump to a bot's terminal
// and come straight back. So the room sits in #app's grid, spanning columns
// two and three, and the sidebar stays exactly where it was.
//
// Data comes from team.ts (the ledger, merged by seq); every rendering decision
// — folding tool calls, continuation rows, presence — is a pure function there,
// pinned by tests. This module owns the DOM and the scroll position.
//
// Self-wiring like the inspector: it registers its own host listener and
// main.ts only calls applyTeamState() on each state push and toggles it.

import {
  send, type StateMessage, type SessionView, type ProjectView,
  type TeamBotView, type TeamDataMessage, type TeamEntryView, type PaneTreeView,
} from "./bridge.js";
import {
  requestTeam, subscribeTeam, cachedTeam, ingestTeamData,
  foldFeed, groupRows, presenceOf, rosterSort, anyWorking, unreadCount, teamSummary, roomEmptyState,
  type FeedRow, type Presence,
} from "./team.js";
import { appendRich, hhmm } from "./text.js";
import { elapsedSpan, agoSpan } from "./elapsed.js";
import { buildComposer, type Composer } from "./mention-input.js";
import { showNewBotDialog } from "./new-bot-dialog.js";
import { showBotMenu } from "./bot-menu.js";
import type { MentionTarget } from "./mention.js";
import { createBotFace, normalizeLook, type BotFace, type FaceState } from "./bot-face.js";
import { confirmDialog } from "./confirm.js";
import type { TeamTaskView } from "./bridge.js";

// ---- Pure rendering decisions ---------------------------------------------

/** The class list for a message row. Exported so the shape of a row — which
 *  kinds get the "your post" fill, which continue the previous one — is pinned
 *  without a DOM. */
export function feedRowClass(row: FeedRow, pending = false, failed = false): string {
  if (row.kind === "workfold") return "tf-work";
  const e = row.entry;
  if (e.kind === "system") return "tf-sys";
  if (e.kind === "work") return "work";
  const cls = ["tf-msg", `tf-msg--${e.kind}`];
  if (row.cont) cls.push("tf-msg--cont");
  if (e.kind === "user" && e.delivered === false) cls.push("tf-msg--held");
  if (pending) cls.push("tf-msg--pending");
  if (failed) cls.push("tf-msg--failed");
  return cls.join(" ");
}

/** The letter in a bot's avatar circle. */
export function avatarInitial(name: string): string {
  const t = name.trim();
  if (t.length === 0) return "?";
  const cp = t.codePointAt(0)!;
  return String.fromCodePoint(cp).toUpperCase();
}

/** What a user row's recipients strip says: "to Ada, Bo", or "to everyone" —
 *  which is also where a post naming nobody goes (each bot then decides
 *  whether it's for them). */
export function recipientsLabel(to: TeamEntryView["to"]): string {
  if (to === undefined || to === "everyone" || to.length === 0) return "to everyone";
  return "to " + to.join(", ");
}

/** The word next to a system row's dot. `event` is the host's label; the
 *  text carries the sentence, so this only picks the tone. */
export function systemTone(event: TeamEntryView["event"]): "calm" | "attention" | "error" {
  switch (event) {
    case "waiting":
    case "permission":
    case "trust":                              // a bot's start-up question, answered from the card
    case "task.review": return "attention";   // the lead is asking you to confirm
    case "error": return "error";
    default: return "calm";
  }
}

/** The word on the task pane's pill for a board status. */
export function taskStatusWord(status: TeamTaskView["status"] | undefined): string {
  switch (status) {
    case "open": return "in progress";
    case "review": return "confirm?";
    case "done": return "wrapping up";
    default: return "";
  }
}

// ---- Module state ----------------------------------------------------------

let root: HTMLElement | null = null;
let feedEl: HTMLElement;
let rosterListEl: HTMLElement;
let countsEl: HTMLElement;
let titleEl: HTMLElement;
let jumpEl: HTMLButtonElement;
let truncEl: HTMLElement;
let emptyEl: HTMLElement;
let mainEl: HTMLElement;
let composer: Composer | null = null;

/* The task pane under the roster. */
let taskStatusEl: HTMLElement;
let taskEditBtn: HTMLButtonElement;
let taskBodyEl: HTMLElement;
/* Inline editors: setting/renaming the task, and the "not yet" note. */
let taskEditorOpen = false;
let rejectOpen = false;

let projectId: string | null = null;
let lastState: StateMessage | null = null;
let unsubscribe: (() => void) | null = null;
let pollTimer: number | null = null;
let closing = false;

/* Rows whose disclosure survives a re-render, keyed by ledger seq (stable —
 * the ledger is append-only). */
const openFolds = new Set<number>();
const openBeats = new Set<number>();

/* Optimistic user rows: sent, not yet echoed back with a seq. Keyed by the
 * clientId the composer minted. */
type PendingPost = { text: string; to: MentionTarget; sentAt: number; failed: boolean };
const pending = new Map<string, PendingPost>();
const PENDING_TIMEOUT_MS = 20_000;

/* Only rows newer than this animate in; the rest are a repaint. */
let renderedSeq = 0;

/* The animated faces the feed and the roster currently show. Each render
 * rebuilds its DOM, so the faces it mounted last time are disposed first —
 * a face keeps a slot on the shared ticker until then. */
const feedFaces: BotFace[] = [];
const rosterFaces: BotFace[] = [];

function disposeFaces(list: BotFace[]): void {
  for (const f of list) f.dispose();
  list.length = 0;
}

/** The face's state for a bot's presence: the two "needs you" states share
 *  the waiting act, a slept or absent bot sleeps, a finished one just idles. */
export function faceStateOf(p: Presence): FaceState {
  switch (p.state) {
    case "working": return "working";
    case "waiting":
    case "permission": return "waiting";
    case "dormant":
    case "offline": return "asleep";
    default: return "idle";
  }
}

/** A stable per-bot phase so a roster never blinks in unison. */
function faceSeed(bot: TeamBotView): number {
  let h = 0;
  for (const ch of bot.botId) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return h;
}

const POLL_MS = 2000;
const NEAR_BOTTOM_PX = 24;

const changeCbs = new Set<() => void>();

// ---- Public API ------------------------------------------------------------

export function isTeamRoomOpen(id?: string): boolean {
  if (!root || closing) return false;
  return id === undefined || projectId === id;
}

export function onTeamRoomChange(cb: () => void): void {
  changeCbs.add(cb);
}

/** The newest seq the user has looked at in a project's room. The sidebar's
 *  unread count is everything after it. */
export function lastSeenSeq(id: string): number {
  try {
    const raw = localStorage.getItem("perch.team.seen");
    if (!raw) return 0;
    const m = JSON.parse(raw) as Record<string, number>;
    return typeof m[id] === "number" ? m[id] : 0;
  } catch { return 0; }
}

function markSeen(id: string, seq: number): void {
  try {
    const raw = localStorage.getItem("perch.team.seen");
    const m = raw ? (JSON.parse(raw) as Record<string, number>) : {};
    if ((m[id] ?? 0) >= seq) return;
    m[id] = seq;
    localStorage.setItem("perch.team.seen", JSON.stringify(m));
  } catch { /* private window, blocked storage */ }
}

/** Unread rows for a project, for the sidebar's team row. */
export function unreadFor(id: string): number {
  const data = cachedTeam(id);
  if (!data) return 0;
  if (isTeamRoomOpen(id)) return 0;
  return unreadCount(data.entries, lastSeenSeq(id));
}

export function openTeamRoom(id: string): void {
  if (root && projectId === id && !closing) { composer?.focus(); return; }
  if (root) unmount(true);
  projectId = id;
  mount();
  send({ type: "team.room", projectId: id, open: true });
  unsubscribe = subscribeTeam(id, () => render());
  render();
  void requestTeam(id, cachedTeam(id)?.lastSeq).then(() => render());
  schedulePoll();
  notifyChange();
  composer?.focus();
}

export function closeTeamRoom(): void {
  if (!root || closing) return;
  const id = projectId;
  unmount(false);
  if (id) send({ type: "team.room", projectId: id, open: false });
  notifyChange();
}

export function toggleTeamRoom(id: string): void {
  if (isTeamRoomOpen(id)) closeTeamRoom();
  else openTeamRoom(id);
}

/** Every state push lands here. Presence and the roster are DERIVED from the
 *  session list, so the room re-renders whenever a bot's session moved. */
export function applyTeamState(msg: StateMessage): void {
  const prev = lastState;
  lastState = msg;
  if (!root || !projectId) return;
  const project = projectFor(projectId);
  if (!project || !project.team || project.team.bots.length === 0) {
    // The team went away under us (last bot removed). The empty state says so.
    render();
    return;
  }
  if (presenceChanged(prev, msg, project.team.bots)) {
    render();
    void requestTeam(projectId, cachedTeam(projectId)?.lastSeq).then(() => render());
  } else {
    renderHead(project);
    renderRoster(project);
    renderTasks(project);
    composer?.refresh();
  }
  schedulePoll();
}

// ---- Mount / unmount -------------------------------------------------------

function el(tag: string, cls: string, text?: string): HTMLElement {
  const e = document.createElement(tag);
  e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function mount(): void {
  const app = document.getElementById("app");
  if (!app) return;
  root = el("section", "team-room");
  root.setAttribute("role", "region");
  root.setAttribute("aria-label", "Team room");

  // Header: project · Team room · counts · close.
  const head = el("header", "team-room__head");
  titleEl = el("h1", "team-room__title");
  head.appendChild(titleEl);
  countsEl = el("div", "team-room__counts");
  head.appendChild(countsEl);
  const close = document.createElement("button");
  close.type = "button";
  close.className = "team-room__close";
  close.setAttribute("aria-label", "Close team room (Esc)");
  close.title = "Close (Esc)";
  close.textContent = "✕";
  close.addEventListener("click", () => closeTeamRoom());
  head.appendChild(close);
  root.appendChild(head);

  // Empty state (no bots / nothing yet) swaps in for the whole main area.
  emptyEl = el("div", "team-room__empty");
  emptyEl.hidden = true;
  root.appendChild(emptyEl);

  mainEl = el("div", "team-room__main");
  const feedWrap = el("div", "team-room__feedwrap");
  truncEl = el("div", "team-room__truncated", "Older messages aren't shown");
  truncEl.hidden = true;
  feedWrap.appendChild(truncEl);
  feedEl = el("div", "team-feed scroll");
  feedEl.setAttribute("role", "log");
  feedEl.setAttribute("aria-live", "polite");
  feedEl.setAttribute("aria-relevant", "additions");
  feedEl.addEventListener("scroll", () => {
    jumpEl.classList.toggle("inspector__jump--on", !isNearBottom(feedEl));
    if (isNearBottom(feedEl) && projectId) {
      const d = cachedTeam(projectId);
      if (d) markSeen(projectId, d.lastSeq);
    }
  });
  feedWrap.appendChild(feedEl);
  jumpEl = document.createElement("button");
  jumpEl.type = "button";
  jumpEl.className = "team-feed__jump inspector__jump";
  jumpEl.textContent = "↓ Jump to latest";
  jumpEl.addEventListener("click", () => {
    feedEl.scrollTop = feedEl.scrollHeight;
    jumpEl.classList.remove("inspector__jump--on");
  });
  feedWrap.appendChild(jumpEl);
  mainEl.appendChild(feedWrap);

  const roster = el("aside", "team-roster");
  roster.setAttribute("aria-label", "Team members");
  const rHead = el("div", "team-roster__head");
  rHead.appendChild(el("span", "team-roster__label", "Team"));
  const rCount = el("span", "team-roster__count");
  rHead.appendChild(rCount);
  const add = document.createElement("button");
  add.type = "button";
  add.className = "team-roster__add";
  add.textContent = "Add bot";
  add.addEventListener("click", () => {
    const p = projectId ? projectFor(projectId) : null;
    if (p) showNewBotDialog(p);
  });
  rHead.appendChild(add);
  roster.appendChild(rHead);
  rosterListEl = el("div", "team-roster__list");
  roster.appendChild(rosterListEl);

  // The task board: the right column's second pane. One task at a time,
  // each bot's piece, and the confirm step that wraps everyone up.
  const tasks = el("section", "team-tasks");
  tasks.setAttribute("aria-label", "Task board");
  const tHead = el("div", "team-tasks__head");
  tHead.appendChild(el("span", "team-roster__label", "Task"));
  taskStatusEl = el("span", "team-tasks__status");
  tHead.appendChild(taskStatusEl);
  taskEditBtn = document.createElement("button");
  taskEditBtn.type = "button";
  taskEditBtn.className = "team-roster__add";
  taskEditBtn.textContent = "Set task";
  taskEditBtn.addEventListener("click", () => {
    taskEditorOpen = !taskEditorOpen;
    rejectOpen = false;
    render();
  });
  tHead.appendChild(taskEditBtn);
  tasks.appendChild(tHead);
  taskBodyEl = el("div", "team-tasks__body");
  tasks.appendChild(taskBodyEl);
  roster.appendChild(tasks);
  mainEl.appendChild(roster);
  root.appendChild(mainEl);

  composer = buildComposer({
    roster: () => (projectId ? projectFor(projectId)?.team?.bots ?? [] : []),
    onSend: (text, to, clientId) => {
      if (!projectId) return;
      pending.set(clientId, { text, to, sentAt: Date.now(), failed: false });
      send({ type: "team.post", projectId, text, to, clientId });
      render();
      feedEl.scrollTop = feedEl.scrollHeight;
      window.setTimeout(() => {
        const p = pending.get(clientId);
        if (p && !p.failed) { p.failed = true; render(); }
      }, PENDING_TIMEOUT_MS);
    },
  });
  root.appendChild(composer.element);

  app.appendChild(root);
  window.addEventListener("keydown", onEsc, true);
}

function unmount(immediate: boolean): void {
  if (!root) return;
  window.removeEventListener("keydown", onEsc, true);
  if (pollTimer !== null) { clearTimeout(pollTimer); pollTimer = null; }
  unsubscribe?.();
  unsubscribe = null;
  if (projectId) {
    const d = cachedTeam(projectId);
    if (d) markSeen(projectId, d.lastSeq);
  }
  disposeFaces(feedFaces);
  disposeFaces(rosterFaces);
  taskEditorOpen = false;
  rejectOpen = false;
  composer?.dispose();
  composer = null;
  const node = root;
  root = null;
  projectId = null;
  renderedSeq = 0;
  if (immediate) { node.remove(); return; }
  closing = true;
  node.classList.add("team-room--closing");
  const done = () => { node.remove(); closing = false; };
  node.addEventListener("animationend", done, { once: true });
  window.setTimeout(done, 260);   // reduced-motion fallback
}

function onEsc(e: KeyboardEvent): void {
  if (e.key !== "Escape" || !root) return;
  // Anything layered above the room owns Esc first.
  if (document.querySelector(".projects-overlay, .settings-overlay, .settings-page, .mention-pop, .bot-menu, .project-menu, .model-menu")) return;
  e.preventDefault();
  e.stopPropagation();
  closeTeamRoom();
}

function notifyChange(): void {
  for (const cb of changeCbs) cb();
}

// ---- Lookups ---------------------------------------------------------------

function projectFor(id: string): ProjectView | null {
  return lastState?.projects.find((p) => p.id === id) ?? null;
}

function sessionOf(bot: TeamBotView): SessionView | undefined {
  if (!bot.sessionId || !lastState) return undefined;
  return lastState.sessions.find((s) => s.id === bot.sessionId);
}

function botByName(bots: TeamBotView[], from: string, botId?: string): TeamBotView | undefined {
  if (botId) {
    const b = bots.find((x) => x.botId === botId);
    if (b) return b;
  }
  return bots.find((x) => x.nickname.toLowerCase() === from.toLowerCase());
}

function firstLeaf(node: PaneTreeView): Extract<PaneTreeView, { kind: "leaf" }> | null {
  if (node.kind === "leaf") return node;
  for (const c of node.children) {
    const l = firstLeaf(c);
    if (l) return l;
  }
  return null;
}

/** The bot's pane color, for its avatar. Falls back to a stable hash of the
 *  nickname when the tab is gone, so an offline bot keeps its hue. */
function colorIndexFor(bot: TeamBotView | undefined, name: string): number {
  const s = bot ? sessionOf(bot) : undefined;
  const leaf = s ? firstLeaf(s.rootPane) : null;
  if (leaf) return leaf.colorIndex % 6;
  let h = 0;
  for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return h % 6;
}

function presenceChanged(prev: StateMessage | null, next: StateMessage, bots: TeamBotView[]): boolean {
  if (!prev) return true;
  for (const b of bots) {
    if (!b.sessionId) continue;
    const a = prev.sessions.find((s) => s.id === b.sessionId);
    const z = next.sessions.find((s) => s.id === b.sessionId);
    if (presenceOf(a).state !== presenceOf(z).state) return true;
    if ((a?.activityDetail ?? "") !== (z?.activityDetail ?? "")) return true;
  }
  return false;
}

const isNearBottom = (e: HTMLElement) => e.scrollHeight - e.scrollTop - e.clientHeight < NEAR_BOTTOM_PX;

// ---- Polling ---------------------------------------------------------------

function schedulePoll(): void {
  if (pollTimer !== null) { clearTimeout(pollTimer); pollTimer = null; }
  if (!root || !projectId || !lastState) return;
  const project = projectFor(projectId);
  if (!project?.team || !anyWorking(project.team.bots, lastState.sessions)) return;
  const id = projectId;
  pollTimer = window.setTimeout(() => {
    pollTimer = null;
    if (!root || projectId !== id) return;
    void requestTeam(id, cachedTeam(id)?.lastSeq).then(() => { render(); schedulePoll(); });
  }, POLL_MS);
}

// ---- Rendering -------------------------------------------------------------

function render(): void {
  if (!root || !projectId) return;
  const project = projectFor(projectId);
  const data = cachedTeam(projectId);
  const bots = project?.team?.bots ?? [];
  renderHead(project);
  renderRoster(project);
  renderTasks(project);
  composer?.refresh();

  const empty = roomEmptyState({ bots, entries: data?.entries ?? [], pending: !data || data.pending });
  if (empty && pending.size === 0 && bots.length === 0) {
    // No bots at all: the whole room is the invitation to add one.
    emptyEl.hidden = false;
    mainEl.hidden = true;
    if (composer) composer.element.hidden = true;
    emptyEl.replaceChildren();
    emptyEl.appendChild(el("div", "team-room__empty-title", empty.title));
    emptyEl.appendChild(el("div", "team-room__empty-body", empty.body));
    if (project) {
      const cta = document.createElement("button");
      cta.type = "button";
      cta.className = "projects-card__btn projects-card__btn--primary team-room__empty-cta";
      cta.textContent = "Add a bot";
      cta.addEventListener("click", () => showNewBotDialog(project));
      emptyEl.appendChild(cta);
    }
    return;
  }
  emptyEl.hidden = true;
  mainEl.hidden = false;
  if (composer) composer.element.hidden = false;

  truncEl.hidden = !(data?.truncated);
  if (empty && pending.size === 0) {
    // Bots exist but the room has nothing to show — a team pulled onto a
    // fresh machine (the chat is local), or a team nobody has spoken to. Say
    // so INSIDE the feed and keep the roster up: it is the only place a
    // not-running bot can be started from, and hiding it left a pulled team
    // with no way in.
    const note = el("div", "team-feed__empty");
    note.appendChild(el("div", "team-room__empty-title", empty.title));
    note.appendChild(el("div", "team-room__empty-body", empty.body));
    feedEl.replaceChildren(note);
    return;
  }
  renderFeed(data?.entries ?? [], bots);
}

function renderHead(project: ProjectView | null): void {
  titleEl.replaceChildren();
  titleEl.appendChild(el("span", "team-room__project", project?.name ?? ""));
  titleEl.appendChild(el("span", "team-room__sep", "·"));
  titleEl.appendChild(el("span", "team-room__name", "Team room"));

  countsEl.replaceChildren();
  if (!project?.team || !lastState) return;
  const bots = project.team.bots;
  let working = 0, needsYou = 0;
  for (const b of bots) {
    const p = presenceOf(sessionOf(b));
    if (p.state === "working") working++;
    else if (p.state === "waiting" || p.state === "permission") needsYou++;
  }
  if (needsYou > 0) countsEl.appendChild(el("span", "dash__count dash__count--alert", `${needsYou} waiting`));
  if (working > 0) countsEl.appendChild(el("span", "dash__count dash__count--work", `${working} working`));
  if (needsYou === 0 && working === 0 && bots.length > 0)
    countsEl.appendChild(el("span", "dash__count dash__count--muted", teamSummary(bots, lastState.sessions)));
}

function renderRoster(project: ProjectView | null): void {
  disposeFaces(rosterFaces);
  rosterListEl.replaceChildren();
  const count = root?.querySelector<HTMLElement>(".team-roster__count");
  const bots = project?.team?.bots ?? [];
  if (count) count.textContent = bots.length > 0 ? String(bots.length) : "";
  if (!lastState || !project) return;
  for (const bot of rosterSort(bots, lastState.sessions)) {
    const s = sessionOf(bot);
    const p = presenceOf(s);
    const row = document.createElement("button");
    row.type = "button";
    row.className = "roster-bot roster-bot--face";
    row.dataset.state = p.state;
    row.title = bot.sessionId ? `Open ${bot.nickname}'s terminal` : `Start ${bot.nickname} on this machine`;

    // The bot's face carries its state (working, waiting, asleep are acts,
    // not colours); the state word beside it says it in words. The face's
    // circle takes the bot's tag hue in colour mode.
    const lead = el("span", "roster-bot__lead roster-bot__lead--face");
    lead.style.setProperty("--tag", `var(--color-pane-tag-${colorIndexFor(bot, bot.nickname)})`);
    const face = createBotFace(normalizeLook(bot.look), colorIndexFor(bot, bot.nickname), faceStateOf(p), faceSeed(bot));
    lead.appendChild(face.el);
    rosterFaces.push(face);
    row.appendChild(lead);

    const text = el("span", "roster-bot__text");
    text.appendChild(el("span", "roster-bot__nick", bot.nickname));
    const pos = el("span", "roster-bot__pos", bot.positionName);
    if (bot.peerName && bot.peerName.toLowerCase() !== bot.nickname.toLowerCase())
      pos.textContent = `${bot.positionName} · ${bot.peerName}`;
    text.appendChild(pos);
    row.appendChild(text);

    row.appendChild(stateLine(p, s));

    row.addEventListener("click", () => {
      if (!bot.sessionId) {
        // Came with a pull, or its tab was closed: start it here. The room
        // stays; the join shows up in the feed.
        send({ type: "team.bot.start", projectId: project.id, botId: bot.botId });
        return;
      }
      closeTeamRoom();
      send({ type: "session.select", id: bot.sessionId });
    });
    row.addEventListener("contextmenu", (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      showBotMenu(ev.clientX, ev.clientY, project, bot, () => closeTeamRoom());
    });
    rosterListEl.appendChild(row);
  }
}

/** The task pane: the current task, every bot's piece, and the step the
 *  owner owns — set it, confirm it's done, or say not yet. */
function renderTasks(project: ProjectView | null): void {
  if (!taskBodyEl) return;
  const team = project?.team;
  const t = team?.task ?? null;
  const bots = team?.bots ?? [];
  const lead = bots.find((b) => b.botId === team?.lead);
  taskBodyEl.replaceChildren();
  taskStatusEl.textContent = taskStatusWord(t?.status);
  taskStatusEl.dataset.status = t?.status ?? "";
  taskEditBtn.textContent = taskEditorOpen ? "Cancel" : t && t.status !== "done" ? "Change" : "Set task";
  taskEditBtn.hidden = !project || bots.length === 0 || t?.status === "done";

  if (!project) return;
  if (taskEditorOpen) {
    const form = el("form", "team-tasks__editor") as HTMLFormElement;
    const input = document.createElement("textarea");
    input.className = "team-tasks__input";
    input.placeholder = "What does done look like?";
    input.value = t && t.status !== "done" ? t.title : "";
    input.rows = 2;
    form.appendChild(input);
    const actions = el("div", "team-tasks__actions");
    const save = document.createElement("button");
    save.type = "submit";
    save.className = "projects-card__btn projects-card__btn--primary";
    save.textContent = t && t.status !== "done" ? "Rename" : "Set task";
    actions.appendChild(save);
    form.appendChild(actions);
    form.addEventListener("submit", (ev) => {
      ev.preventDefault();
      const title = input.value.trim();
      if (!title) return;
      send({ type: "team.task.set", projectId: project.id, title });
      taskEditorOpen = false;
      render();
    });
    input.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); form.requestSubmit(); }
      if (ev.key === "Escape") { taskEditorOpen = false; render(); }
    });
    taskBodyEl.appendChild(form);
    requestAnimationFrame(() => input.focus());
    return;
  }

  if (!t) {
    taskBodyEl.appendChild(el("p", "team-tasks__empty",
      bots.length === 0 ? "Add a bot first."
        : lead ? `No task yet. ${lead.nickname} leads and sets one with you, or set it here.`
        : "No task yet. Set one here, or make a bot the team lead so it can."));
    return;
  }

  taskBodyEl.appendChild(el("div", "team-tasks__title", t.title));
  const meta = el("div", "team-tasks__meta");
  meta.appendChild(document.createTextNode(`set by ${t.setBy === "you" ? "you" : t.setBy} · `));
  meta.appendChild(agoSpan(t.createdAtMs, true));
  taskBodyEl.appendChild(meta);

  const list = el("ul", "team-tasks__items");
  for (const bot of bots) {
    const item = t.items.find((i) => i.botId === bot.botId);
    const li = el("li", "task-item");
    const dot = el("span", "task-item__dot");
    dot.dataset.status = item?.status ?? "todo";
    dot.setAttribute("aria-hidden", "true");
    li.appendChild(dot);
    const line = el("span", "task-item__line");
    line.appendChild(el("span", "task-item__who", bot.nickname + (bot.botId === team?.lead ? " (lead)" : "") + " "));
    line.appendChild(el("span", item ? "task-item__what" : "task-item__what task-item__what--none", item ? item.title : "no piece yet"));
    li.appendChild(line);
    if (item?.note) li.appendChild(el("span", "task-item__note", item.note));
    list.appendChild(li);
  }
  taskBodyEl.appendChild(list);

  const actions = el("div", "team-tasks__actions");
  const btn = (label: string, primary: boolean, onClick: () => void) => {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "projects-card__btn" + (primary ? " projects-card__btn--primary" : "");
    b.textContent = label;
    b.addEventListener("click", onClick);
    return b;
  };
  const confirmDone = () => {
    void confirmDialog({
      title: "Task done?",
      body: `Every running bot writes what the next task needs into its memory, then its conversation is cleared. "${t.title}" moves to the archive.`,
      confirmLabel: "Confirm done",
    }).then((ok) => { if (ok) send({ type: "team.task.confirm", projectId: project.id }); });
  };
  if (t.status === "review") {
    actions.appendChild(el("span", "team-tasks__say", `${t.reviewBy ?? "The lead"} says it's done.`));
    actions.appendChild(btn("Confirm done", true, confirmDone));
    actions.appendChild(btn("Not yet…", false, () => { rejectOpen = !rejectOpen; render(); }));
    taskBodyEl.appendChild(actions);
    if (rejectOpen) {
      const form = el("form", "team-tasks__editor") as HTMLFormElement;
      const input = document.createElement("textarea");
      input.className = "team-tasks__input";
      input.placeholder = "What's missing? (goes to the lead)";
      input.rows = 2;
      form.appendChild(input);
      const a2 = el("div", "team-tasks__actions");
      const sendBtn = document.createElement("button");
      sendBtn.type = "submit";
      sendBtn.className = "projects-card__btn projects-card__btn--primary";
      sendBtn.textContent = "Send";
      a2.appendChild(sendBtn);
      form.appendChild(a2);
      form.addEventListener("submit", (ev) => {
        ev.preventDefault();
        send({ type: "team.task.reject", projectId: project.id, note: input.value.trim() || undefined });
        rejectOpen = false;
        render();
      });
      input.addEventListener("keydown", (ev) => {
        if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); form.requestSubmit(); }
        if (ev.key === "Escape") { rejectOpen = false; render(); }
      });
      taskBodyEl.appendChild(form);
      requestAnimationFrame(() => input.focus());
    }
  } else if (t.status === "open") {
    actions.appendChild(btn("Mark done", false, confirmDone));
    taskBodyEl.appendChild(actions);
  } else if (t.status === "done") {
    const wrap = el("div", "team-tasks__wrap");
    wrap.appendChild(document.createTextNode("Wrapping up: "));
    for (const bot of bots) {
      const pending = t.wrapping.includes(bot.botId);
      wrap.appendChild(el("span", pending ? "pending" : "", `${bot.nickname}${pending ? "…" : " ✓"}`));
    }
    taskBodyEl.appendChild(wrap);
  }
}

/** "working · 2m" / "waiting for you" / "idle · 4m" / "asleep". */
function stateLine(p: Presence, s: SessionView | undefined): HTMLElement {
  const line = el("span", "roster-bot__state");
  line.dataset.state = p.state;
  line.appendChild(el("span", "roster-bot__word", p.word));
  if (p.state === "working" && s && s.turnStartMs > 0) {
    line.appendChild(el("span", "roster-bot__dotsep", "·"));
    line.appendChild(elapsedSpan(s.turnStartMs, true));
  } else if ((p.state === "done" || p.state === "idle") && s && s.doneAtMs > 0) {
    line.appendChild(el("span", "roster-bot__dotsep", "·"));
    line.appendChild(agoSpan(s.doneAtMs, true));
  }
  return line;
}

/** Nicknames whose start-up question has been answered (a later trusted /
 *  exited row), so their trust card renders without its buttons. Rebuilt on
 *  every feed render from the rows themselves — no second source of truth. */
const answeredTrust = new Set<string>();

function renderFeed(entries: TeamEntryView[], bots: TeamBotView[]): void {
  answeredTrust.clear();
  for (const e of entries) {
    if (e.kind === "system" && (e.event === "trusted" || e.event === "exited") && Array.isArray(e.to))
      for (const n of e.to) answeredTrust.add(n);
  }
  const pinned = isNearBottom(feedEl) || feedEl.childElementCount === 0;
  const prevTop = feedEl.scrollTop;

  // Drop optimistic rows the host has echoed back.
  for (const e of entries) {
    if (e.kind === "user" && e.clientId && pending.has(e.clientId)) pending.delete(e.clientId);
  }

  // A successful delivery is the post's own status line, not a row of its
  // own; only the parked→flushed case (OnAgentUp) writes one, and even that
  // reads as chatter next to the post it belongs to.
  const shown = entries.filter((e) => !(e.kind === "system" && e.event === "delivered"));
  disposeFaces(feedFaces);
  const rows = groupRows(foldFeed(shown));
  const names = bots.map((b) => b.nickname);
  const frag = document.createDocumentFragment();
  let maxSeq = renderedSeq;
  for (const row of rows) {
    const node = renderRow(row, bots, names);
    if (row.seq > renderedSeq) node.classList.add("row-enter");
    if (row.seq > maxSeq) maxSeq = row.seq;
    frag.appendChild(node);
  }
  for (const [clientId, p] of pending) frag.appendChild(renderPending(clientId, p, names));
  feedEl.replaceChildren(frag);
  renderedSeq = maxSeq;

  if (pinned) feedEl.scrollTop = feedEl.scrollHeight;
  else feedEl.scrollTop = prevTop;
  jumpEl.classList.toggle("inspector__jump--on", !isNearBottom(feedEl));
  if (isNearBottom(feedEl) && projectId) {
    const d = cachedTeam(projectId);
    if (d) markSeen(projectId, d.lastSeq);
  }
}

function avatar(bot: TeamBotView | undefined, name: string): HTMLElement {
  const a = el("span", "tf-msg__avatar");
  a.setAttribute("aria-hidden", "true");
  if (name === "you") {
    a.classList.add("tf-msg__avatar--you");
    a.textContent = ">";
    return a;
  }
  a.style.setProperty("--tag", `var(--color-pane-tag-${colorIndexFor(bot, name)})`);
  if (bot) {
    // The bot's face, in the state it is in right now — the feed is live, so
    // a bot that is working is seen working next to what it said.
    a.classList.add("tf-msg__avatar--face");
    const face = createBotFace(normalizeLook(bot.look), colorIndexFor(bot, name),
      faceStateOf(presenceOf(sessionOf(bot))), faceSeed(bot));
    a.appendChild(face.el);
    feedFaces.push(face);
    return a;
  }
  a.textContent = avatarInitial(name);
  return a;
}

function displayName(from: string, bot: TeamBotView | undefined): string {
  if (from === "you") return "You";
  if (from === "perch") return "Perch";
  return bot?.nickname ?? from;
}

function renderRow(row: FeedRow, bots: TeamBotView[], names: string[]): HTMLElement {
  if (row.kind === "workfold") return renderFold(row, bots);
  const e = row.entry;
  if (e.kind === "system") return renderSystem(e, bots);
  if (e.kind === "work") return renderWork(e);

  const bot = botByName(bots, e.from, e.botId);
  const node = el("div", feedRowClass(row));
  node.dataset.seq = String(e.seq);
  // Column one: the avatar on a fresh message, a hover-revealed time on a
  // continuation (the avatar above already says who).
  if (row.cont) node.appendChild(el("span", "tf-msg__time tf-msg__time--cont", hhmm(e.ts)));
  else node.appendChild(avatar(bot, e.from));

  const body = el("div", "tf-msg__body");
  if (!row.cont) {
    const head = el("div", "tf-msg__head");
    head.appendChild(el("span", "tf-msg__name", displayName(e.from, bot)));
    if (e.kind === "peer") {
      head.appendChild(el("span", "tf-msg__arrow", "→"));
      const target = Array.isArray(e.to) ? e.to.join(", ") : e.to === "everyone" ? "everyone" : "";
      head.appendChild(el("span", "tf-msg__name", target));
    } else if (bot && e.kind !== "user") {
      head.appendChild(el("span", "tf-msg__pos", bot.positionName));
    }
    if (e.kind === "note") head.appendChild(el("span", "tf-msg__tag", "to the room"));
    head.appendChild(el("span", "tf-msg__time", hhmm(e.ts)));
    body.appendChild(head);
  }

  const text = el("div", "tf-msg__text");
  appendRich(text, e.text, names);
  body.appendChild(text);
  if (e.kind === "beat") {
    // Long beats clamp; click opens in place. Keyed by seq so the disclosure
    // survives the next poll's repaint.
    if (openBeats.has(e.seq)) node.classList.add("tf-msg--open");
    node.addEventListener("click", (ev) => {
      if ((ev.target as HTMLElement).closest("button, a")) return;
      if (!node.classList.contains("tf-msg--expandable")) return;
      const open = node.classList.toggle("tf-msg--open");
      if (open) openBeats.add(e.seq); else openBeats.delete(e.seq);
    });
    requestAnimationFrame(() => {
      if (text.scrollHeight > text.clientHeight + 2 || node.classList.contains("tf-msg--open"))
        node.classList.add("tf-msg--expandable");
    });
  }

  if (e.kind === "user") {
    const strip = el("div", "tf-msg__to", recipientsLabel(e.to));
    if (e.delivered === false) strip.textContent += " · waiting for the bot";
    body.appendChild(strip);
  }
  node.appendChild(body);
  return node;
}

function renderPending(clientId: string, p: PendingPost, names: string[]): HTMLElement {
  const node = el("div", "tf-msg tf-msg--user tf-msg--pending" + (p.failed ? " tf-msg--failed" : ""));
  node.dataset.clientId = clientId;
  node.appendChild(avatar(undefined, "you"));
  const body = el("div", "tf-msg__body");
  const head = el("div", "tf-msg__head");
  head.appendChild(el("span", "tf-msg__name", "You"));
  head.appendChild(el("span", "tf-msg__time", hhmm(new Date(p.sentAt).toISOString())));
  body.appendChild(head);
  const text = el("div", "tf-msg__text");
  appendRich(text, p.text, names);
  body.appendChild(text);
  const to = p.to === null ? undefined : p.to;
  const strip = el("div", "tf-msg__to", p.failed ? "Not sent — the host didn't answer" : `${recipientsLabel(to)} · sending…`);
  body.appendChild(strip);
  node.appendChild(body);
  return node;
}

function renderFold(row: Extract<FeedRow, { kind: "workfold" }>, bots: TeamBotView[]): HTMLElement {
  const bot = botByName(bots, row.from, row.botId);
  const open = openFolds.has(row.seq);
  const node = el("div", "tf-work");
  node.dataset.seq = String(row.seq);
  node.setAttribute("aria-expanded", String(open));

  // Rail in the avatar column, the line beside it — same two-column grid as
  // a message, so the fold's text aligns under the messages' text.
  node.appendChild(el("span", "tf-work__rail", "│"));
  const line = el("div", "tf-work__line");
  const summary = document.createElement("button");
  summary.type = "button";
  summary.className = "tf-work__summary";
  summary.textContent = `${displayName(row.from, bot)} ${row.summary}`;
  summary.title = open ? "Hide the steps" : "Show the steps";
  summary.addEventListener("click", () => {
    const now = node.getAttribute("aria-expanded") !== "true";
    node.setAttribute("aria-expanded", String(now));
    rows.hidden = !now;
    if (now) openFolds.add(row.seq); else openFolds.delete(row.seq);
  });
  line.appendChild(summary);
  line.appendChild(el("span", "tf-work__time", hhmm(row.entries[row.entries.length - 1].ts)));
  if (bot?.sessionId) {
    const openBtn = document.createElement("button");
    openBtn.type = "button";
    openBtn.className = "tf-work__open";
    openBtn.textContent = "Open journal";
    openBtn.title = `Go to ${bot.nickname}'s terminal`;
    openBtn.addEventListener("click", () => {
      closeTeamRoom();
      send({ type: "session.select", id: bot.sessionId });
    });
    line.appendChild(openBtn);
  }
  node.appendChild(line);

  const rows = el("div", "tf-work__rows");
  rows.hidden = !open;
  for (const e of row.entries) rows.appendChild(renderWork(e));
  node.appendChild(rows);
  return node;
}

/** One tool call, in the journal's own row shape (same classes, same CSS). */
function renderWork(e: TeamEntryView): HTMLElement {
  const repeat = e.repeat ?? 1;
  const w = el("div", repeat > 1 ? "work work--repeat" : "work");
  w.appendChild(el("span", "work__time", hhmm(e.ts)));
  w.appendChild(el("span", "work__rail", "│"));
  const what = el("span", "work__what");
  what.appendChild(el("span", "work__verb", e.verb ?? "did"));
  if (e.target) what.appendChild(el("span", "work__target", e.target));
  w.appendChild(what);
  if (repeat > 1) w.appendChild(el("span", "work__repeat", `×${repeat}`));
  else if (e.note) w.appendChild(el("span", "work__note", e.note));
  return w;
}

function renderSystem(e: TeamEntryView, bots: TeamBotView[]): HTMLElement {
  const node = el("div", "tf-sys");
  node.dataset.seq = String(e.seq);
  node.dataset.tone = systemTone(e.event);
  if (e.event) node.dataset.event = e.event;
  const dot = el("span", "tf-sys__dot");
  dot.setAttribute("aria-hidden", "true");
  node.appendChild(dot);
  node.appendChild(el("span", "tf-sys__text", e.text));
  const bot = botByName(bots, e.from, e.botId);
  // A start-up question ("trust this folder?") is answered right here: the
  // card carries the two answers until a later row says it was answered.
  const asked = Array.isArray(e.to) ? e.to[0] : undefined;
  const askedBot = asked ? bots.find((b) => b.nickname === asked) : undefined;
  if (e.event === "trust" && askedBot && !answeredTrust.has(askedBot.nickname)) {
    const project = projectId ? projectFor(projectId) : null;
    const answer = (choice: "trust" | "exit", label: string, title: string) => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "tf-sys__open";
      b.textContent = label;
      b.title = title;
      b.addEventListener("click", () => {
        if (!project) return;
        send({ type: "team.bot.answer", projectId: project.id, botId: askedBot.botId, answer: choice });
        node.querySelectorAll("button").forEach((x) => { (x as HTMLButtonElement).disabled = true; });
      });
      return b;
    };
    node.appendChild(answer("trust", "Trust folder", `Answer "Yes, I trust this folder" for ${askedBot.nickname}`));
    node.appendChild(answer("exit", "Don't", `Answer "No, exit" for ${askedBot.nickname}`));
    if (askedBot.sessionId) {
      const open = document.createElement("button");
      open.type = "button";
      open.className = "tf-sys__open";
      open.textContent = "Open";
      open.title = `Go to ${askedBot.nickname}'s terminal`;
      open.addEventListener("click", () => {
        closeTeamRoom();
        send({ type: "session.select", id: askedBot.sessionId });
      });
      node.appendChild(open);
    }
  }
  // Rows that need the owner IN the bot's terminal — it is asking something,
  // or a typed post is sitting there unsent — carry the door to it.
  if ((e.event === "waiting" || e.event === "permission" || e.event === "undelivered") && bot?.sessionId) {
    const open = document.createElement("button");
    open.type = "button";
    open.className = "tf-sys__open";
    open.textContent = "Open";
    open.title = `Go to ${bot.nickname}'s terminal`;
    open.addEventListener("click", () => {
      closeTeamRoom();
      send({ type: "session.select", id: bot.sessionId });
    });
    node.appendChild(open);
  }
  node.appendChild(el("span", "tf-sys__time", hhmm(e.ts)));
  return node;
}

// ---- Harness hook ----------------------------------------------------------

/** Feed a fixture in as if the host had sent it. Same as team.ts's
 *  ingestTeamData; re-exported so a harness needs one import. */
export function feedTeamFixture(msg: TeamDataMessage): void {
  ingestTeamData(msg);
}
