// The Team room: one conversation across a project's bots, with you in it.
//
// A full-height surface laid over the workspace + inspector columns, NOT a
// pane leaf and NOT a fixed-inset page. A leaf lives in one session's tree
// (closing that tab would kill the room); a fixed page would hide the sidebar,
// and the sidebar's bot rows are the fastest way to jump to a bot's terminal
// and come straight back. So the room sits in #app's grid, spanning columns
// two and three, and the sidebar stays exactly where it was.
//
// Three columns: the feed, the task cards (one per open task, the lead keeps
// them current, you confirm), and the roster. Everything you have to decide —
// a bot's permission prompt, its question, its start-up question, a task's
// confirm — is a card in the feed with buttons, so you never go to a terminal
// to answer.
//
// Data comes from team.ts (the ledger, merged by seq); every rendering decision
// — folding tool calls, continuation rows, presence, which cards are answered —
// is a pure function there, pinned by tests. This module owns the DOM and the
// scroll position.
//
// Self-wiring like the inspector: it registers its own host listener and
// main.ts only calls applyTeamState() on each state push and toggles it.

import {
  send, type StateMessage, type SessionView, type ProjectView,
  type TeamBotView, type TeamDataMessage, type TeamEntryView, type TeamTaskView, type TeamView,
} from "./bridge.js";
import {
  requestTeam, subscribeTeam, cachedTeam, ingestTeamData, requestTeamImage,
  foldFeed, groupRows, presenceOf, rosterSort, anyWorking, unreadCount, teamSummary, roomEmptyState,
  visibleEntries, reactionsFor, taskOrder, answeredSet, landedSet, handoffLabel, REACTIONS,
  type FeedRow, type Presence, type ReactionPill,
} from "./team.js";
import { appendRich, appendBlocks, hhmm, imageLabel } from "./text.js";
import { artefactDocument } from "./artefact-export.js";
import { elapsedSpan, agoSpan } from "./elapsed.js";
import { buildComposer, type Composer } from "./mention-input.js";
import { showNewBotDialog } from "./new-bot-dialog.js";
import { showBotMenu } from "./bot-menu.js";
import type { MentionTarget } from "./mention.js";
import { createBotFace, normalizeLook, type BotFace, type FaceState } from "./bot-face.js";
import { confirmDialog } from "./confirm.js";
import { showToast } from "./toast.js";
import type {
  TeamPasteDataMessage, TeamArtefactDataMessage, TeamArtefactIndexMessage, TeamArtefactItem,
} from "./bridge.js";

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
  if (e.kind === "peer" && handoffLabel(e.note) === "question") cls.push("tf-msg--question");
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

/** The tone of a system row. `event` is the host's label; the text carries
 *  the sentence, so this only picks the tone: attention for anything that
 *  waits on you, error for what went wrong, calm for the rest. */
export function systemTone(event: TeamEntryView["event"]): "calm" | "attention" | "error" {
  switch (event) {
    case "waiting":
    case "permission":                         // a bot's permission prompt, answered from the card
    case "ask":                                // a bot's question, answered from the card
    case "trust":                              // a bot's start-up question, answered from the card
    case "task.review": return "attention";   // the lead is asking you to confirm
    case "permission.expired":                 // the card ran out of time
      return "attention";
    case "error":
    case "peer.failed":                        // a bot's message never left
    case "denied": return "error";            // auto mode blocked the bot
    default: return "calm";
  }
}

/** The word a card wears, or "" for a row that is only narration. Cards are
 *  what you ACT on, and each kind has its own colour in the CSS keyed on the
 *  same event — the word is there so the colour is never the only carrier. */
export function cardKind(event: TeamEntryView["event"]): string {
  switch (event) {
    case "permission": return "permission";
    case "ask": return "question";
    case "trust": return "trust";
    case "task.review": return "review";
    case "denied": return "blocked";
    default: return "";
  }
}

/** The word on a task card's pill for a board status. */
export function taskStatusWord(status: TeamTaskView["status"] | undefined): string {
  switch (status) {
    case "open": return "in progress";
    case "review": return "confirm?";
    case "done": return "wrapping up";
    default: return "";
  }
}

/** The permission card's details: the tool input as JSON, prettified, at most
 *  `maxLines` lines (the rest folded into a last "…" line). Unparseable input
 *  is shown as it came. */
export function permissionDetails(summary: string | undefined, maxLines = 8): string {
  if (!summary) return "";
  let text = summary;
  try { text = JSON.stringify(JSON.parse(summary), null, 2); } catch { /* not JSON: show as-is */ }
  const lines = text.split("\n");
  if (lines.length <= maxLines) return text;
  return lines.slice(0, maxLines - 1).join("\n") + "\n…";
}

// ---- Module state ----------------------------------------------------------

let root: HTMLElement | null = null;
let feedEl: HTMLElement;
let rosterListEl: HTMLElement;
let countsEl: HTMLElement;
let titleEl: HTMLElement;
let activityBtn: HTMLButtonElement;
let jumpEl: HTMLButtonElement;
let truncEl: HTMLElement;
let emptyEl: HTMLElement;
let mainEl: HTMLElement;
let composer: Composer | null = null;

/* The task column. */
let taskCountEl: HTMLElement;
let newTaskBtn: HTMLButtonElement;
let taskBodyEl: HTMLElement;
/* Inline editors, by task id: renaming a card, the "not yet" note; plus the
 * new-task editor at the top of the column. */
let renameFor: string | null = null;
let rejectFor: string | null = null;
let newTaskOpen = false;

/* The artefacts panel under the cards: what is on screen, what the "Recent"
 * menu offers, and which one we asked the host for. */
let artefactTitleEl: HTMLElement;
let artefactMetaEl: HTMLElement;
let artefactMenuBtn: HTMLButtonElement;
let artefactTabBtn: HTMLButtonElement;
let artefactMenuEl: HTMLElement;
let artefactBodyEl: HTMLElement;
let artefactMenuOpen = false;
/* How tall the owner dragged the cards half, in px; null = let the cards take
 * the height they need. Kept per machine, like the activity toggle. */
let taskSplitPx: number | null = null;
const ARTEFACT_MIN_PX = 220;
const SPLIT_KEY = "perch.team.tasksSplit";

function readTaskSplit(): number | null {
  try {
    const raw = localStorage.getItem(SPLIT_KEY);
    const n = raw === null ? NaN : Number(raw);
    return Number.isFinite(n) && n > 0 ? n : null;
  } catch { return null; }
}
function writeTaskSplit(px: number | null): void {
  try { if (px === null) localStorage.removeItem(SPLIT_KEY); else localStorage.setItem(SPLIT_KEY, String(Math.round(px))); }
  catch { /* private mode: the split just doesn't survive the session */ }
}
function setTaskSplit(px: number | null): void {
  taskSplitPx = px;
  if (!taskBodyEl) return;
  taskBodyEl.style.flex = px === null ? "" : `0 0 ${Math.round(px)}px`;
}

let artefactIndex: TeamArtefactItem[] = [];
let shownArtefact: TeamArtefactDataMessage | null = null;
let artefactLoading: string | null = null;

let projectId: string | null = null;
let lastState: StateMessage | null = null;
let unsubscribe: (() => void) | null = null;
let pollTimer: number | null = null;
let closing = false;

/* Rows whose disclosure survives a re-render, keyed by ledger seq (stable —
 * the ledger is append-only). */
const openFolds = new Set<number>();
const openBeats = new Set<number>();
const openDetails = new Set<number>();

/* Optimistic user rows: sent, not yet echoed back with a seq. Keyed by the
 * clientId the composer minted. */
type PendingPost = { text: string; to: MentionTarget; sentAt: number; failed: boolean };
const pending = new Map<string, PendingPost>();
const PENDING_TIMEOUT_MS = 20_000;

/* Reactions you clicked that the host hasn't echoed yet: seq → emoji set. */
const optimisticReactions = new Map<number, Set<string>>();

/* Only rows newer than this animate in; the rest are a repaint. */
let renderedSeq = 0;
/** What the feed currently shows, so a poll that brought nothing new (or a
 *  presence tick that only touched the roster) doesn't rebuild the rows —
 *  every rebuild is a chance to lose the reader's place. */
let renderedSig = "";

/* Tool activity in the feed is off until you ask for it. Per machine. */
let showActivity = readActivityPref();

function readActivityPref(): boolean {
  try { return localStorage.getItem("perch.team.activity") === "1"; } catch { return false; }
}
function writeActivityPref(on: boolean): void {
  try { localStorage.setItem("perch.team.activity", on ? "1" : "0"); } catch { /* blocked storage */ }
}

/* The animated faces the feed and the roster currently show. Each render
 * rebuilds its DOM, so the faces it mounted last time are disposed first —
 * a face keeps a slot on the shared ticker until then. */
const feedFaces: BotFace[] = [];
/* Which faces belong to which feed row, so a row that survives a render keeps
 * its avatar animating and a row that goes away stops paying for one. */
let feedFacesByKey = new Map<string, BotFace[]>();
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
  shownArtefact = null;
  artefactIndex = [];
  artefactLoading = null;
  artefactMenuOpen = false;
  mount();
  send({ type: "team.room", projectId: id, open: true });
  send({ type: "team.artefact.list", projectId: id });
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
 *  session list, so the room re-renders whenever a bot's session moved; the
 *  task cards ride in the same push. */
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

function button(cls: string, label: string, onClick: () => void, title?: string): HTMLButtonElement {
  const b = document.createElement("button");
  b.type = "button";
  b.className = cls;
  b.textContent = label;
  if (title) b.title = title;
  b.addEventListener("click", (ev) => { ev.stopPropagation(); onClick(); });
  return b;
}

function mount(): void {
  const app = document.getElementById("app");
  if (!app) return;
  showActivity = readActivityPref();   // per machine; read when the room opens, not at load
  root = el("section", "team-room");
  root.setAttribute("role", "region");
  root.setAttribute("aria-label", "Team room");

  // Header: project · Team room · counts · activity toggle · close.
  const head = el("header", "team-room__head");
  titleEl = el("h1", "team-room__title");
  head.appendChild(titleEl);
  countsEl = el("div", "team-room__counts");
  head.appendChild(countsEl);
  activityBtn = button("team-room__activity", "", () => {
    showActivity = !showActivity;
    writeActivityPref(showActivity);
    renderedSeq = Number.MAX_SAFE_INTEGER;   // a toggle is a repaint, not new rows
    render();
    renderedSeq = cachedTeam(projectId ?? "")?.lastSeq ?? 0;
  });
  head.appendChild(activityBtn);
  const close = document.createElement("button");
  close.type = "button";
  close.className = "team-room__close";
  close.setAttribute("aria-label", "Close team room (Esc)");
  close.title = "Close (Esc)";
  close.textContent = "✕";
  close.addEventListener("click", () => closeTeamRoom());
  head.appendChild(close);
  root.appendChild(head);

  // Empty state (no bots at all) swaps in for the whole main area.
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

  // The task column: one card per task on the board. The lead keeps the
  // pieces current; you open a task, confirm it done, or say not yet.
  const tasks = el("section", "team-tasks");
  tasks.setAttribute("aria-label", "Task board");
  const tHead = el("div", "team-tasks__head");
  tHead.appendChild(el("span", "team-roster__label", "Tasks"));
  taskCountEl = el("span", "team-roster__count");
  tHead.appendChild(taskCountEl);
  newTaskBtn = button("team-roster__add", "New task", () => {
    newTaskOpen = !newTaskOpen;
    renameFor = null;
    rejectFor = null;
    render();
  });
  tHead.appendChild(newTaskBtn);
  tasks.appendChild(tHead);
  taskBodyEl = el("div", "team-tasks__body scroll");
  tasks.appendChild(taskBodyEl);
  setTaskSplit(readTaskSplit());

  // The cards take the height they need and the panel takes what is left; drag
  // the line between them to change that, and it is remembered per machine.
  const grip = el("div", "team-tasks__grip");
  grip.setAttribute("role", "separator");
  grip.setAttribute("aria-orientation", "horizontal");
  grip.setAttribute("aria-label", "Resize the task cards");
  grip.addEventListener("pointerdown", (ev) => {
    ev.preventDefault();
    grip.setPointerCapture(ev.pointerId);
    const startY = ev.clientY;
    const startH = taskBodyEl.getBoundingClientRect().height;
    const column = tasks.getBoundingClientRect().height;
    const move = (m: PointerEvent) => {
      // Neither half may vanish: the cards keep one row, the panel its minimum.
      const max = Math.max(48, column - ARTEFACT_MIN_PX - 44);
      setTaskSplit(Math.min(max, Math.max(48, startH + (m.clientY - startY))));
    };
    const up = () => {
      grip.removeEventListener("pointermove", move);
      grip.removeEventListener("pointerup", up);
      writeTaskSplit(taskSplitPx);
    };
    grip.addEventListener("pointermove", move);
    grip.addEventListener("pointerup", up);
  });
  grip.addEventListener("dblclick", () => { setTaskSplit(null); writeTaskSplit(null); });
  tasks.appendChild(grip);

  // Under the cards: the artefacts panel. Anything a bot writes that is too
  // long to read as a message — a draft ticket, a table, a plan — lands here,
  // opened from its card in the feed or from this panel's own list of recent
  // ones. It fills the space the task cards leave.
  const arte = el("section", "team-arte");
  arte.setAttribute("aria-label", "Artefacts");
  const aHead = el("div", "team-arte__head");
  artefactTitleEl = el("span", "team-arte__title", "Artefacts");
  aHead.appendChild(artefactTitleEl);
  artefactMetaEl = el("span", "team-arte__meta");
  aHead.appendChild(artefactMetaEl);
  // The panel is a strip; a plan or a draft ticket wants a whole tab. This
  // opens the artefact on screen as its own browser tab, so it can sit next to
  // the work it's about instead of being scrolled in a corner.
  artefactTabBtn = button("team-roster__add team-arte__tab", "Open in a tab", openArtefactInTab);
  artefactTabBtn.title = "Open this artefact as its own tab";
  aHead.appendChild(artefactTabBtn);
  artefactMenuBtn = button("team-roster__add team-arte__recent", "Recent", () => {
    artefactMenuOpen = !artefactMenuOpen;
    if (artefactMenuOpen && projectId) send({ type: "team.artefact.list", projectId });
    renderArtefacts();
  });
  aHead.appendChild(artefactMenuBtn);
  arte.appendChild(aHead);
  artefactMenuEl = el("div", "team-arte__menu");
  artefactMenuEl.hidden = true;
  arte.appendChild(artefactMenuEl);
  artefactBodyEl = el("div", "team-arte__body scroll");
  arte.appendChild(artefactBodyEl);
  tasks.appendChild(arte);
  mainEl.appendChild(tasks);

  const roster = el("aside", "team-roster");
  roster.setAttribute("aria-label", "Team members");
  const rHead = el("div", "team-roster__head");
  rHead.appendChild(el("span", "team-roster__label", "Team"));
  const rCount = el("span", "team-roster__count");
  rHead.appendChild(rCount);
  rHead.appendChild(button("team-roster__add", "Add bot", () => {
    const p = projectId ? projectFor(projectId) : null;
    if (p) showNewBotDialog(p);
  }));
  roster.appendChild(rHead);
  rosterListEl = el("div", "team-roster__list");
  roster.appendChild(rosterListEl);
  mainEl.appendChild(roster);
  root.appendChild(mainEl);

  composer = buildComposer({
    roster: () => (projectId ? projectFor(projectId)?.team?.bots ?? [] : []),
    onPaste: () => { if (projectId) send({ type: "team.paste", projectId }); },
    onSend: (text, to, clientId, image) => {
      if (!projectId) return;
      pending.set(clientId, { text: text || "(picture)", to, sentAt: Date.now(), failed: false });
      send({ type: "team.post", projectId, text, to, clientId, image });
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

/** The host answered a paste: attach the saved picture to the draft, or say
 *  why there was nothing to attach. */
export function applyPasteResult(msg: TeamPasteDataMessage): void {
  if (!composer || !projectId || msg.projectId !== projectId) return;
  if (msg.path) composer.attachImage(msg.path);
  else showToast(msg.error || "No picture on the clipboard.", "warn");
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
  renameFor = null;
  rejectFor = null;
  newTaskOpen = false;
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

/** A bot's colour: its place in the team, so the first six bots are six
 *  different hues and each one's avatar, roster row and name in the chat
 *  agree.
 *
 *  It used to come from the bot's TAB colour, and a tab is created with the
 *  project's default — so three bots in a row wore the same hue and the
 *  colour said nothing about who was speaking. */
function colorIndexFor(bot: TeamBotView | undefined, name: string): number {
  const roster = projectId ? projectFor(projectId)?.team?.bots ?? [] : [];
  const at = bot ? roster.findIndex((b) => b.botId === bot.botId) : -1;
  if (at >= 0) return at % 6;
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

/** Jump to a bot's terminal and come back later. */
function openTerminal(bot: TeamBotView): void {
  if (!bot.sessionId) return;
  closeTeamRoom();
  send({ type: "session.select", id: bot.sessionId });
}

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
  renderArtefacts();
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

  activityBtn.textContent = showActivity ? "Hide activity" : "Show activity";
  activityBtn.setAttribute("aria-pressed", String(showActivity));
  activityBtn.title = showActivity ? "Hide the bots' tool calls" : "Show the bots' tool calls between messages";

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
  const count = root?.querySelector<HTMLElement>(".team-roster .team-roster__count");
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
    const pos = el("span", "roster-bot__pos", bot.positionName + (bot.botId === project.team?.lead ? " · lead" : ""));
    if (bot.peerName && bot.peerName.toLowerCase() !== bot.nickname.toLowerCase())
      pos.textContent += ` · ${bot.peerName}`;
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
      openTerminal(bot);
    });
    row.addEventListener("contextmenu", (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      showBotMenu(ev.clientX, ev.clientY, project, bot, () => closeTeamRoom());
    });
    rosterListEl.appendChild(row);
  }
}

// ---- Task cards ------------------------------------------------------------

/** A one-field editor (new task title, a rename, the "not yet" note). */
function editor(placeholder: string, initial: string, submitLabel: string,
  onSubmit: (value: string) => void, onCancel: () => void, allowEmpty = false): HTMLFormElement {
  const form = el("form", "team-tasks__editor") as HTMLFormElement;
  const input = document.createElement("textarea");
  input.className = "team-tasks__input";
  input.placeholder = placeholder;
  input.value = initial;
  input.rows = 2;
  form.appendChild(input);
  const actions = el("div", "team-tasks__actions");
  const save = document.createElement("button");
  save.type = "submit";
  save.className = "projects-card__btn projects-card__btn--primary";
  save.textContent = submitLabel;
  actions.appendChild(save);
  actions.appendChild(button("projects-card__btn", "Cancel", onCancel));
  form.appendChild(actions);
  form.addEventListener("submit", (ev) => {
    ev.preventDefault();
    const value = input.value.trim();
    if (!value && !allowEmpty) return;
    onSubmit(value);
  });
  input.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); form.requestSubmit(); }
    if (ev.key === "Escape") { ev.stopPropagation(); onCancel(); }
  });
  requestAnimationFrame(() => input.focus());
  return form;
}

/** The task column: one card per task on the board, what needs you first. */
function renderTasks(project: ProjectView | null): void {
  if (!taskBodyEl) return;
  const team = project?.team;
  const bots = team?.bots ?? [];
  const tasks = taskOrder(team?.tasks ?? []);
  taskBodyEl.replaceChildren();
  taskCountEl.textContent = tasks.length > 0 ? String(tasks.length) : "";
  newTaskBtn.textContent = newTaskOpen ? "Cancel" : "New task";
  newTaskBtn.hidden = !project || bots.length === 0;
  if (!project) return;

  if (newTaskOpen) {
    taskBodyEl.appendChild(editor("What does done look like?", "", "Set task",
      (title) => { send({ type: "team.task.set", projectId: project.id, title }); newTaskOpen = false; render(); },
      () => { newTaskOpen = false; render(); }));
  }
  if (tasks.length === 0) {
    if (!newTaskOpen) {
      const lead = bots.find((b) => b.botId === team?.lead);
      taskBodyEl.appendChild(el("p", "team-tasks__empty",
        bots.length === 0 ? "Add a bot first."
          : lead ? `No tasks yet. Set one here, or ${lead.nickname} will.`
          : "No tasks yet. Set one here, or make a bot the team lead so it can."));
    }
    return;
  }
  for (const t of tasks) taskBodyEl.appendChild(taskCard(project, team!, t));
}

/** How much of a long message is shown before it is folded. */
const BLOCK_CLAMP_PX = 200;
/** …and how tall a single unsplittable block (one big table) may be while
 *  folded, before it is cut with a fade. */
const SINGLE_BLOCK_MAX_PX = 320;

/** Fold a long message on a BLOCK boundary rather than mid-height: a table cut
 *  through its third row, or half a bullet, reads as broken rather than as
 *  "there is more". Whole blocks are hidden instead, and the row keeps its
 *  click-to-open. Returns whether anything is folded away.
 *
 *  Only for the rich bodies (a table, a list, code…). A plain paragraph beat
 *  still clamps by line count in CSS, which is exactly right for prose. */
function clampBlocks(text: HTMLElement, open: boolean): boolean {
  if (!text.classList.contains("md--rich")) return false;
  const kids = Array.from(text.children) as HTMLElement[];
  for (const k of kids) k.hidden = false;
  text.classList.remove("md--cut");
  if (open || kids.length === 0) return kids.length > 0;
  const top = text.getBoundingClientRect().top;
  let cut = -1;
  for (let i = 1; i < kids.length; i++) {
    if (kids[i].getBoundingClientRect().bottom - top > BLOCK_CLAMP_PX) { cut = i; break; }
  }
  if (cut >= 0) {
    for (let i = cut; i < kids.length; i++) kids[i].hidden = true;
    return true;
  }
  // One block, taller than the fold on its own: cut it with a fade, since
  // there is no boundary to fold at.
  if (text.getBoundingClientRect().height > SINGLE_BLOCK_MAX_PX) { text.classList.add("md--cut"); return true; }
  return false;
}

// ---- Artefacts --------------------------------------------------------------

/** A short word for what an artefact is, from its file extension. The card and
 *  the panel both say it, so a table isn't mistaken for a page. */
export function artefactKindWord(kind: string | undefined): string {
  switch ((kind ?? "").toLowerCase()) {
    case "md": return "document";
    case "html": return "page";
    case "csv": return "table";
    case "json": return "data";
    case "sql": return "query";
    case "diff": return "diff";
    case "log": return "log";
    case "": return "note";
    default: return kind!.toLowerCase();
  }
}

/** Ask the host for one artefact and show it. */
function openArtefactById(id: string): void {
  if (!projectId) return;
  artefactLoading = id;
  artefactMenuOpen = false;
  send({ type: "team.artefact.open", projectId, id });
  renderArtefacts();
}

/** The host answered with an artefact's text. */
export function applyArtefact(msg: TeamArtefactDataMessage): void {
  if (projectId && msg.projectId !== projectId) return;
  artefactLoading = null;
  shownArtefact = msg;
  renderArtefacts();
}

/** The host answered with the recent list for the panel's menu. */
export function applyArtefactIndex(msg: TeamArtefactIndexMessage): void {
  if (projectId && msg.projectId !== projectId) return;
  artefactIndex = msg.items ?? [];
  // Nothing open yet: show the newest, so the panel is never a blank half of
  // the room when there is something to read.
  if (!shownArtefact && !artefactLoading && artefactIndex.length > 0) openArtefactById(artefactIndex[0].id);
  else renderArtefacts();
}

function renderArtefacts(): void {
  if (!artefactBodyEl) return;
  const a = shownArtefact;
  artefactTitleEl.textContent = a && !a.error ? (a.title ?? "Untitled") : "Artefacts";
  artefactTitleEl.title = a?.title ?? "";
  artefactMetaEl.textContent = a && !a.error
    ? `${artefactKindWord(a.kind)} · from ${a.from}` + (a.truncated ? " · shortened" : "")
    : "";
  artefactMenuBtn.hidden = artefactIndex.length === 0 && !a;
  artefactMenuBtn.textContent = artefactMenuOpen ? "Close list" : "Recent";
  // Only offer the tab when there is a document to put in it.
  artefactTabBtn.hidden = !a || !!a.error;

  artefactMenuEl.hidden = !artefactMenuOpen;
  if (artefactMenuOpen) {
    artefactMenuEl.replaceChildren();
    if (artefactIndex.length === 0) artefactMenuEl.appendChild(el("p", "team-tasks__empty", "Nothing yet."));
    for (const it of artefactIndex) {
      const row = button("team-arte__item", "", () => openArtefactById(it.id));
      if (it.id === a?.id) row.classList.add("team-arte__item--on");
      row.appendChild(el("span", "team-arte__item-title", it.title));
      row.appendChild(el("span", "team-arte__item-meta", `${artefactKindWord(it.kind)} · ${it.from}`));
      artefactMenuEl.appendChild(row);
    }
  }

  artefactBodyEl.replaceChildren();
  if (artefactLoading) { artefactBodyEl.appendChild(el("p", "team-tasks__empty", "Opening…")); return; }
  if (!a) {
    artefactBodyEl.appendChild(el("p", "team-tasks__empty",
      "Anything a bot writes that is longer than a message opens here — a draft ticket, a table, a plan."));
    return;
  }
  if (a.error) { artefactBodyEl.appendChild(el("p", "team-tasks__empty", a.error)); return; }
  artefactBodyEl.appendChild(renderArtefactDoc(a));
}

/** One artefact's body, as an element. Shared by the panel and by "Open in a
 *  tab" so the tab is the same document, not a second rendering of it.
 *  Markdown and plain prose read as a document; everything structured (a table
 *  file, a query, a diff) stays exactly as the bot wrote it. */
function renderArtefactDoc(a: TeamArtefactDataMessage): HTMLElement {
  const body = el("div", "team-arte__doc");
  const content = a.content ?? "";
  if (a.kind === "md" || a.kind === "txt" || !a.kind) appendBlocks(body, content, []);
  else body.appendChild(el("pre", "team-arte__pre", content));
  return body;
}

/** Send the artefact on screen to the host as a finished HTML document, which
 *  it writes out and opens as its own tab. Rendered here rather than host-side
 *  because this is the side that owns the markdown renderer and the theme. */
function openArtefactInTab(): void {
  const a = shownArtefact;
  if (!projectId || !a || a.error) return;
  const title = a.title ?? "Artefact";
  const meta = `${artefactKindWord(a.kind)} · from ${a.from}` + (a.truncated ? " · shortened" : "");
  send({
    type: "team.artefact.tab",
    projectId,
    id: a.id,
    title,
    html: artefactDocument(title, meta, renderArtefactDoc(a).outerHTML),
  });
}

/** An artefact's row in the feed: a card that opens it beside the chat. */
function renderArtefactCard(e: TeamEntryView, bots: TeamBotView[]): HTMLElement {
  const node = el("div", "tf-sys tf-sys--card");
  node.dataset.seq = String(e.seq);
  node.dataset.event = "artefact";
  node.dataset.tone = "calm";
  node.appendChild(el("span", "tf-sys__kind", "artefact"));
  const body = el("div", "tf-sys__body");
  const who = botByName(bots, e.from, e.botId)?.nickname ?? e.from;
  body.appendChild(el("span", "tf-sys__text tf-sys__text--wrap", `${who} — ${e.text}`));
  if (e.summary) body.appendChild(el("span", "tf-arte__summary", e.summary));
  const actions = el("div", "tf-sys__actions");
  actions.appendChild(button("tf-sys__open tf-sys__open--primary", "Open", () => {
    if (e.target) openArtefactById(e.target);
  }, "Show it beside the chat"));
  actions.appendChild(el("span", "tf-sys__answered", artefactKindWord(e.note)));
  body.appendChild(actions);
  node.appendChild(body);
  node.appendChild(el("span", "tf-sys__time", hhmm(e.ts)));
  return node;
}

function taskCard(project: ProjectView, team: TeamView, t: TeamTaskView): HTMLElement {
  const bots = team.bots;
  const card = el("article", "task-card");
  card.dataset.status = t.status;
  card.dataset.taskId = t.id;

  // Head: the title (click to rename while it's open) and the status pill.
  const head = el("div", "task-card__head");
  if (renameFor === t.id) {
    head.appendChild(editor("Task title", t.title, "Rename",
      (title) => { send({ type: "team.task.rename", projectId: project.id, taskId: t.id, title }); renameFor = null; render(); },
      () => { renameFor = null; render(); }));
  } else {
    const title = button("task-card__title", t.title, () => {
      if (t.status === "done") return;
      renameFor = t.id; rejectFor = null; newTaskOpen = false; render();
    }, t.status === "done" ? undefined : "Rename this task");
    head.appendChild(title);
    const pill = el("span", "task-card__status", taskStatusWord(t.status));
    pill.dataset.status = t.status;
    head.appendChild(pill);
  }
  card.appendChild(head);

  const meta = el("div", "task-card__meta");
  meta.appendChild(document.createTextNode(`set by ${t.setBy} · `));
  meta.appendChild(agoSpan(t.createdAtMs, true));
  card.appendChild(meta);

  // Pieces: one line per bot that has one on this card.
  const list = el("ul", "team-tasks__items");
  const items = t.items.filter((i) => i.title);
  for (const item of items) {
    const bot = bots.find((b) => b.botId === item.botId);
    const li = el("li", "task-item");
    const dot = el("span", "task-item__dot");
    dot.dataset.status = item.status;
    dot.setAttribute("aria-hidden", "true");
    li.appendChild(dot);
    const line = el("span", "task-item__line");
    line.appendChild(el("span", "task-item__who", (bot?.nickname ?? item.bot) + (item.botId === team.lead ? " (lead)" : "") + " "));
    line.appendChild(el("span", "task-item__what", item.title));
    li.appendChild(line);
    if (item.note) li.appendChild(el("span", "task-item__note", item.note));
    list.appendChild(li);
  }
  if (items.length === 0) list.appendChild(el("li", "task-item task-item--none", "No pieces yet — the lead splits it."));
  card.appendChild(list);

  const actions = el("div", "team-tasks__actions");
  const confirmDone = () => {
    void confirmDialog({
      title: "Task done?",
      body: `The bots on "${t.title}" write what the next task needs into their memory, then their conversation is cleared. The card moves to the archive.`,
      confirmLabel: "Confirm done",
    }).then((ok) => { if (ok) send({ type: "team.task.confirm", projectId: project.id, taskId: t.id }); });
  };
  if (t.status === "review") {
    actions.appendChild(el("span", "team-tasks__say", `${t.reviewBy ?? "The lead"} says it's done.`));
    const ok = button("projects-card__btn projects-card__btn--primary", "Confirm done", confirmDone);
    actions.appendChild(ok);
    actions.appendChild(button("projects-card__btn", "Not yet…", () => { rejectFor = rejectFor === t.id ? null : t.id; renameFor = null; render(); }));
    card.appendChild(actions);
    if (rejectFor === t.id) {
      card.appendChild(editor("What's missing? (goes to the lead)", "", "Send",
        (note) => { send({ type: "team.task.reject", projectId: project.id, taskId: t.id, note: note || undefined }); rejectFor = null; render(); },
        () => { rejectFor = null; render(); }, true));
    }
  } else if (t.status === "open") {
    actions.appendChild(button("projects-card__btn", "Mark done", confirmDone));
    // The escape hatch: a card nobody is going to finish, or one that is done
    // in all but name. Nothing is asked of the bots — it just goes.
    actions.appendChild(button("team-tasks__remove", "Remove", () => {
      void confirmDialog({
        title: "Remove this card?",
        body: `"${t.title}" leaves the board. The bots are not told and nothing is reset — use "Mark done" for that.`,
        confirmLabel: "Remove",
      }).then((ok) => { if (ok) send({ type: "team.task.close", projectId: project.id, taskId: t.id }); });
    }, "Take this card off the board"));
    card.appendChild(actions);
  } else if (t.status === "done") {
    const wrap = el("div", "team-tasks__wrap");
    wrap.appendChild(document.createTextNode("Wrapping up: "));
    const on = bots.filter((b) => t.wrapping.includes(b.botId) || items.some((i) => i.botId === b.botId));
    for (const bot of on) {
      const pendingWrap = t.wrapping.includes(bot.botId);
      wrap.appendChild(el("span", pendingWrap ? "pending" : "", `${bot.nickname}${pendingWrap ? "…" : " ✓"}`));
    }
    if (on.length === 0) wrap.appendChild(el("span", "", "done"));
    card.appendChild(wrap);
  }
  return card;
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

// ---- Feed ------------------------------------------------------------------

/* Per-render lookups the row renderers read: which cards are answered, and
 * the reactions hanging under each row. Rebuilt from the rows themselves on
 * every render — no second source of truth. */
let answered = answeredSet([]);
/* Posts that landed after being parked (a bot that had to start first). */
let landed = new Set<number>();
let reactions = new Map<number, ReactionPill[]>();

/** Whether the last feed render was pinned to the bottom. A picture that
 *  finishes loading after that render re-pins the feed, so late bytes never
 *  leave the reader a screen above the newest message. */
let lastPinned = true;

function repinIfPinned(): void {
  if (lastPinned && feedEl) feedEl.scrollTop = feedEl.scrollHeight;
}

/** A row's identity across renders: its ledger seq (a folded run of tool calls
 *  is keyed by its first). Stable, because the ledger only ever appends. */
export function rowKey(row: FeedRow): string {
  return row.kind === "workfold" ? `w${row.seq}` : `e${row.seq}`;
}

/** Everything about a row that changes what it looks like. Same signature =
 *  the node on screen is still correct and is left alone — which is what keeps
 *  a card's buttons alive under the owner's finger. */
export function rowSig(
  row: FeedRow,
  ctx: {
    answered: { perms: Set<string>; asks: Set<string>; trust: Set<string> };
    reactions: Map<number, ReactionPill[]>;
    /* Reactions you clicked that the host has not echoed yet — they are on
     * screen, so they belong in the signature. */
    optimistic: Map<number, Set<string>>;
    /* Posts that landed late: a row saying "waiting for the bot" has to stop
     * saying it once the post goes in. */
    landed: Set<number>;
    /* Nickname (and slug) → the tab that bot runs in. A reused row keeps the
     * handlers it was built with, so its "Open" button holds the session id of
     * that moment: if the bot moved tabs, the row has to be rebuilt. */
    sessions: Map<string, string>;
    openFolds: Set<number>;
    openBeats: Set<number>;
  },
): string {
  const pills = (ctx.reactions.get(row.seq) ?? []).map((p) => `${p.emoji}${p.who.join(",")}`).join("")
    + "|" + [...(ctx.optimistic.get(row.seq) ?? [])].join("");
  const tab = (who: string | undefined) => (who ? ctx.sessions.get(who) ?? "" : "");
  if (row.kind === "workfold")
    return `w|${row.entries.length}|${row.summary}|${row.cont}|${ctx.openFolds.has(row.seq)}|${pills}|${tab(row.from)}`;
  const e = row.entry;
  const note = e.note ?? "";
  const done = e.kind === "system"
    ? `${ctx.answered.perms.has(note)}${ctx.answered.asks.has(note)}` +
      (Array.isArray(e.to) ? e.to.map((n) => ctx.answered.trust.has(n)).join("") : "")
    : "";
  return [
    "e", e.kind, e.seq, e.text.length, e.summary?.length ?? 0, e.event ?? "", e.delivered ?? "",
    row.cont, ctx.openBeats.has(e.seq), done, e.image ?? "", pills, ctx.landed.has(e.seq),
    tab(e.from), tab(Array.isArray(e.to) ? e.to[0] : undefined),
  ].join("|");
}

function renderFeed(entries: TeamEntryView[], bots: TeamBotView[]): void {
  answered = answeredSet(entries);
  landed = landedSet(entries);
  reactions = reactionsFor(entries);
  // Drop the optimistic reactions the host has echoed.
  for (const [seq, emojis] of optimisticReactions) {
    const pills = reactions.get(seq) ?? [];
    for (const emoji of [...emojis]) if (pills.some((p) => p.emoji === emoji && p.who.includes("you"))) emojis.delete(emoji);
    if (emojis.size === 0) optimisticReactions.delete(seq);
  }
  const pinned = isNearBottom(feedEl) || feedEl.childElementCount === 0;
  lastPinned = pinned;
  const prevTop = feedEl.scrollTop;

  // Drop optimistic rows the host has echoed back.
  for (const e of entries) {
    if (e.kind === "user" && e.clientId && pending.has(e.clientId)) pending.delete(e.clientId);
  }

  // Nothing to redraw? Then don't. The poll answers every two seconds and
  // the roster ticks on every presence change; neither is a reason to tear
  // the rows down under a reader who scrolled up.
  const last = entries.length > 0 ? entries[entries.length - 1].seq : 0;
  const failed = [...pending.values()].filter((p) => p.failed).length;
  const sig = `${entries.length}:${last}:${pending.size}:${failed}:${showActivity}:${optimisticReactions.size}:${bots.length}`;
  if (sig === renderedSig && feedEl.childElementCount > 0) {
    jumpEl.classList.toggle("inspector__jump--on", !isNearBottom(feedEl));
    return;
  }
  renderedSig = sig;

  // Scrolled up: remember which row sits at the top of the view and where,
  // and put it back there after the rebuild. Restoring a pixel offset is
  // not enough — anything above that grew or shrank would shift the reader.
  let anchorSeq: string | null = null;
  let anchorDelta = 0;
  if (!pinned) {
    const feedTop = feedEl.getBoundingClientRect().top;
    for (const child of Array.from(feedEl.children) as HTMLElement[]) {
      const r = child.getBoundingClientRect();
      if (r.bottom > feedTop && child.dataset.seq) {
        anchorSeq = child.dataset.seq;
        anchorDelta = r.top - feedTop;
        break;
      }
    }
  }

  const shown = visibleEntries(entries, showActivity);
  const rows = groupRows(foldFeed(shown));
  const names = bots.map((b) => b.nickname);

  // Rows are REUSED, not rebuilt. A bot at work adds a row every couple of
  // seconds, and tearing the whole feed down each time destroys the button
  // under the owner's cursor: a press that starts on the old node and ends on
  // its replacement fires no click at all, which is how an Allow could be
  // pressed and simply not happen. Each row carries a key and a signature of
  // everything its rendering depends on; only a row whose signature changed is
  // built again.
  const alive = new Map<string, HTMLElement>();
  for (const child of Array.from(feedEl.children) as HTMLElement[])
    if (child.dataset.key) alive.set(child.dataset.key, child);

  const sessions = new Map<string, string>();
  for (const b of bots) { const id = b.sessionId ?? ""; sessions.set(b.nickname, id); sessions.set(b.botId, id); }
  const sigCtx = { answered, reactions, optimistic: optimisticReactions, landed, sessions, openFolds, openBeats };
  const nodes: HTMLElement[] = [];
  const nextFaces = new Map<string, BotFace[]>();
  let maxSeq = renderedSeq;

  /* Build (or reuse) one row, carrying its faces with it. A rebuilt row's old
   * faces are disposed; a kept row's keep running. */
  const place = (key: string, sig: string, build: () => HTMLElement, enter: boolean) => {
    const prev = alive.get(key);
    if (prev && prev.dataset.sig === sig) {
      nodes.push(prev);
      nextFaces.set(key, feedFacesByKey.get(key) ?? []);
      return;
    }
    for (const f of feedFacesByKey.get(key) ?? []) f.dispose();
    const before = feedFaces.length;
    const node = build();
    node.dataset.key = key;
    node.dataset.sig = sig;
    if (enter) node.classList.add("row-enter");
    nodes.push(node);
    nextFaces.set(key, feedFaces.slice(before));
  };

  for (const row of rows) {
    if (row.seq > maxSeq) maxSeq = row.seq;
    place(rowKey(row), rowSig(row, sigCtx), () => renderRow(row, bots, names), row.seq > renderedSeq);
  }
  for (const [clientId, p] of pending)
    place(`p${clientId}`, `p:${p.failed}:${p.text.length}`, () => renderPending(clientId, p, names), false);

  // Faces of rows that are gone stop; everything still on screen keeps moving.
  for (const [key, list] of feedFacesByKey) if (!nextFaces.has(key)) for (const f of list) f.dispose();
  feedFacesByKey = nextFaces;
  feedFaces.length = 0;
  for (const list of nextFaces.values()) feedFaces.push(...list);

  feedEl.replaceChildren(...nodes);
  renderedSeq = maxSeq;

  if (pinned) feedEl.scrollTop = feedEl.scrollHeight;
  else
  {
    const again = anchorSeq ? (feedEl.querySelector(`[data-seq="${anchorSeq}"]`) as HTMLElement | null) : null;
    if (again) {
      const feedTop = feedEl.getBoundingClientRect().top;
      feedEl.scrollTop += (again.getBoundingClientRect().top - feedTop) - anchorDelta;
    } else {
      feedEl.scrollTop = prevTop;
    }
  }
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
  if (e.kind === "artefact") return renderArtefactCard(e, bots);
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
    // A name wears its bot's colour — the same hue as its avatar and its
    // roster row, so a glance down the feed says who is talking.
    const named = (who: string, of: TeamBotView | undefined) => {
      const span = el("span", "tf-msg__name", who);
      if (of) span.dataset.colorIndex = String(colorIndexFor(of, who));
      return span;
    };
    head.appendChild(named(displayName(e.from, bot), bot));
    if (e.kind === "peer") {
      head.appendChild(el("span", "tf-msg__arrow", "→"));
      const target = Array.isArray(e.to) ? e.to.join(", ") : e.to === "everyone" ? "everyone" : "";
      const toBot = Array.isArray(e.to) && e.to.length === 1 ? bots.find((b) => b.nickname === e.to![0]) : undefined;
      head.appendChild(named(target, toBot));
      // The hand-off label the sender put in front: what this message IS.
      const label = handoffLabel(e.note);
      if (label) {
        const chip = el("span", "tf-chip", label);
        chip.dataset.kind = label;
        head.appendChild(chip);
      }
    } else if (bot && e.kind !== "user") {
      head.appendChild(el("span", "tf-msg__pos", bot.positionName));
    }
    if (e.kind === "note") head.appendChild(el("span", "tf-msg__tag", "to the room"));
    head.appendChild(el("span", "tf-msg__time", hhmm(e.ts)));
    body.appendChild(head);
  }

  const text = el("div", "tf-msg__text");
  const images = appendBlocks(text, e.text, names);
  body.appendChild(text);
  if (e.image && !images.includes(e.image)) images.push(e.image);
  if (images.length > 0) body.appendChild(renderThumbs(images, hhmm(e.ts)));
  if (e.kind === "beat") {
    // Long beats clamp; click opens in place. Keyed by seq so the disclosure
    // survives the next poll's repaint.
    if (openBeats.has(e.seq)) node.classList.add("tf-msg--open");
    node.addEventListener("click", (ev) => {
      if ((ev.target as HTMLElement).closest("button, a, img")) return;
      if (!node.classList.contains("tf-msg--expandable")) return;
      const open = node.classList.toggle("tf-msg--open");
      if (open) openBeats.add(e.seq); else openBeats.delete(e.seq);
      clampBlocks(text, open);
    });
    requestAnimationFrame(() => {
      const open = node.classList.contains("tf-msg--open");
      const long = clampBlocks(text, open);
      if (long || text.scrollHeight > text.clientHeight + 2 || open)
        node.classList.add("tf-msg--expandable");
    });
  }

  if (e.kind === "user") {
    const strip = el("div", "tf-msg__to", recipientsLabel(e.to));
    if (e.delivered === false && !landed.has(e.seq)) strip.textContent += " · waiting for the bot";
    body.appendChild(strip);
  }

  body.appendChild(renderReactions(e.seq));
  node.appendChild(body);
  node.appendChild(reactBar(e.seq));
  return node;
}

/** The pills under a row: each emoji once, with who put it there. Your own
 *  pill is highlighted so your feedback stands out from the bots'. */
function renderReactions(seq: number): HTMLElement {
  const wrap = el("div", "tf-react");
  const pills = [...(reactions.get(seq) ?? [])];
  for (const emoji of optimisticReactions.get(seq) ?? []) {
    const pill = pills.find((p) => p.emoji === emoji);
    if (pill) { if (!pill.who.includes("you")) pill.who = [...pill.who, "you"]; }
    else pills.push({ emoji, who: ["you"] });
  }
  for (const p of pills) {
    const mine = p.who.includes("you");
    const b = button("tf-react__pill" + (mine ? " tf-react__pill--mine" : ""), `${p.emoji} ${p.who.length}`,
      () => react(seq, p.emoji), p.who.map((w) => (w === "you" ? "You" : w)).join(", "));
    b.setAttribute("aria-label", `${p.emoji} from ${p.who.join(", ")}`);
    wrap.appendChild(b);
  }
  wrap.hidden = pills.length === 0;
  return wrap;
}

/** The hover/focus bar with the room's four reactions. */
function reactBar(seq: number): HTMLElement {
  const bar = el("div", "tf-reactbar");
  bar.setAttribute("role", "group");
  bar.setAttribute("aria-label", "React");
  for (const emoji of REACTIONS) bar.appendChild(button("tf-reactbar__btn", emoji, () => react(seq, emoji), `React ${emoji}`));
  return bar;
}

function react(seq: number, emoji: string): void {
  if (!projectId) return;
  let set = optimisticReactions.get(seq);
  if (!set) { set = new Set(); optimisticReactions.set(seq, set); }
  set.add(emoji);
  send({ type: "team.react", projectId, seq, emoji });
  render();
}

/** Thumbnails for the pictures a row refers to; click opens the full size. */
function renderThumbs(paths: string[], time: string): HTMLElement {
  const wrap = el("div", "tf-thumbs");
  const pid = projectId ?? "";
  for (const path of paths) {
    const fig = el("figure", "tf-thumb");
    const img = document.createElement("img");
    img.className = "tf-thumb__img";
    img.alt = imageLabel(path);
    img.loading = "lazy";
    fig.appendChild(img);
    const cap = el("figcaption", "tf-thumb__caption", imageLabel(path));
    cap.title = path;
    fig.appendChild(cap);
    fig.addEventListener("click", (ev) => { ev.stopPropagation(); if (img.src) openImageLightbox(img.src, `${imageLabel(path)} · ${time}`); });
    img.addEventListener("load", repinIfPinned, { once: true });
    void requestTeamImage(pid, path).then((src) => {
      if (src) { img.src = src; fig.classList.add("tf-thumb--loaded"); }
      else { fig.classList.add("tf-thumb--missing"); cap.textContent = `${imageLabel(path)} — couldn't load`; }
    });
    wrap.appendChild(fig);
  }
  return wrap;
}

/* Same overlay surface as the journal's image lightbox (.settings-overlay /
 * .settings-card.image-lightbox), which also enrolls it in the web-pane
 * suppression for free. */
let lightboxOpen = false;
function openImageLightbox(src: string, caption: string): void {
  if (lightboxOpen) return;
  lightboxOpen = true;
  const overlay = el("div", "settings-overlay");
  const card = el("div", "settings-card image-lightbox");
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  const img = document.createElement("img");
  img.className = "image-lightbox__img";
  img.src = src;
  img.alt = caption;
  card.appendChild(img);
  card.appendChild(el("div", "image-lightbox__caption", caption));
  overlay.appendChild(card);
  document.body.appendChild(overlay);
  let settled = false;
  const finish = () => {
    if (settled) return;
    settled = true;
    lightboxOpen = false;
    window.removeEventListener("keydown", onKey, true);
    overlay.classList.add("settings-overlay--closing");
    overlay.addEventListener("animationend", () => overlay.remove(), { once: true });
    window.setTimeout(() => overlay.remove(), 260);
  };
  function onKey(ev: KeyboardEvent) {
    if (ev.key === "Escape") { ev.preventDefault(); ev.stopPropagation(); finish(); }
  }
  overlay.addEventListener("mousedown", (ev) => { if (ev.target === overlay) finish(); });
  window.addEventListener("keydown", onKey, true);
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
    line.appendChild(button("tf-work__open", "Open journal", () => openTerminal(bot), `Go to ${bot.nickname}'s terminal`));
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

// ---- System rows and cards -------------------------------------------------

/** A system row: one quiet line, or — for anything you answer here — a card
 *  with its buttons. The buttons go once a later row says it was answered. */
function renderSystem(e: TeamEntryView, bots: TeamBotView[]): HTMLElement {
  const node = el("div", "tf-sys");
  node.dataset.seq = String(e.seq);
  node.dataset.tone = systemTone(e.event);
  if (e.event) node.dataset.event = e.event;
  const dot = el("span", "tf-sys__dot");
  dot.setAttribute("aria-hidden", "true");
  node.appendChild(dot);
  // A row you have to act on wears its kind: a coloured frame (CSS, keyed on
  // data-event) and the word itself, so the colour is never the only carrier.
  const kindWord = cardKind(e.event);
  if (kindWord) {
    node.classList.add("tf-sys--card");
    node.appendChild(el("span", "tf-sys__kind", kindWord));
  }
  const bot = botByName(bots, e.from, e.botId);
  const project = projectId ? projectFor(projectId) : null;
  const asked = Array.isArray(e.to) ? e.to[0] : undefined;
  const askedBot = asked ? bots.find((b) => b.nickname === asked) : undefined;
  const target = askedBot ?? bot;
  const openBtn = (b: TeamBotView | undefined) =>
    b?.sessionId ? button("tf-sys__open", "Open", () => openTerminal(b), `Go to ${b.nickname}'s terminal`) : null;
  const disableAll = () => node.querySelectorAll("button").forEach((x) => { (x as HTMLButtonElement).disabled = true; });

  // A permission prompt: the tool and what it wants, the raw input behind a
  // details line, Allow / Deny. The host hands the answer to Claude Code's
  // hook; the terminal never has to be visited.
  if (e.event === "permission" && e.note && project) {
    const id = e.note;
    node.classList.add("tf-sys--card");
    const body = el("div", "tf-sys__body");
    body.appendChild(el("span", "tf-sys__text tf-sys__text--wrap", e.text));
    if (e.summary) {
      const details = el("pre", "tf-sys__details", permissionDetails(e.summary));
      details.hidden = !openDetails.has(e.seq);
      const more = button("tf-sys__more", details.hidden ? "details" : "hide details", () => {
        details.hidden = !details.hidden;
        more.textContent = details.hidden ? "details" : "hide details";
        if (details.hidden) openDetails.delete(e.seq); else openDetails.add(e.seq);
      });
      body.appendChild(more);
      body.appendChild(details);
    }
    if (!answered.perms.has(id)) {
      const actions = el("div", "tf-sys__actions");
      actions.appendChild(button("tf-sys__open tf-sys__open--primary", "Allow", () => {
        send({ type: "team.perm.answer", projectId: project.id, id, decision: "allow" }); disableAll();
      }, "Let it run this once"));
      actions.appendChild(button("tf-sys__open", "Deny", () => {
        send({ type: "team.perm.answer", projectId: project.id, id, decision: "deny" }); disableAll();
      }, "Don't let it run"));
      const o = openBtn(target);
      if (o) actions.appendChild(o);
      body.appendChild(actions);
    } else {
      body.appendChild(el("span", "tf-sys__answered", "answered"));
    }
    node.appendChild(body);
    node.appendChild(el("span", "tf-sys__time", hhmm(e.ts)));
    return node;
  }

  // A bot's question (`perch team ask`): its choices as buttons, or a line to
  // type into. The answer goes back to that bot as a post.
  if (e.event === "ask" && e.note && project) {
    const id = e.note;
    node.classList.add("tf-sys--card");
    const body = el("div", "tf-sys__body");
    const who = target?.nickname ?? e.from;
    body.appendChild(el("span", "tf-sys__text tf-sys__text--wrap", `${who} asks: ${e.text}`));
    if (!answered.asks.has(id)) {
      const actions = el("div", "tf-sys__actions");
      const choices = (e.choices ?? []).map((c) => c.trim()).filter((c) => c.length > 0);
      if (choices.length > 0) {
        choices.forEach((c, i) => actions.appendChild(button("tf-sys__open" + (i === 0 ? " tf-sys__open--primary" : ""), c, () => {
          send({ type: "team.ask.answer", projectId: project.id, id, answer: c }); disableAll();
        })));
      } else {
        const form = el("form", "tf-sys__form") as HTMLFormElement;
        const input = document.createElement("input");
        input.type = "text";
        input.className = "tf-sys__input";
        input.placeholder = "Your answer";
        input.setAttribute("aria-label", `Answer ${who}`);
        input.addEventListener("keydown", (ev) => ev.stopPropagation());
        form.appendChild(input);
        const sendBtn = document.createElement("button");
        sendBtn.type = "submit";
        sendBtn.className = "tf-sys__open tf-sys__open--primary";
        sendBtn.textContent = "Send";
        form.appendChild(sendBtn);
        form.addEventListener("submit", (ev) => {
          ev.preventDefault();
          const answer = input.value.trim();
          if (!answer) return;
          send({ type: "team.ask.answer", projectId: project.id, id, answer });
          disableAll();
        });
        actions.appendChild(form);
      }
      const o = openBtn(target);
      if (o) actions.appendChild(o);
      body.appendChild(actions);
    } else {
      body.appendChild(el("span", "tf-sys__answered", "answered"));
    }
    node.appendChild(body);
    node.appendChild(el("span", "tf-sys__time", hhmm(e.ts)));
    return node;
  }

  // A framed row wraps its sentence; a narration line still clips to one.
  node.appendChild(el("span", kindWord ? "tf-sys__text tf-sys__text--wrap" : "tf-sys__text", e.text));

  // A start-up question ("trust this folder?") is answered right here: the
  // card carries the two answers until a later row says it was answered.
  if (e.event === "trust" && askedBot && project && !answered.trust.has(askedBot.nickname)) {
    node.appendChild(button("tf-sys__open tf-sys__open--primary", "Trust folder", () => {
      send({ type: "team.bot.answer", projectId: project.id, botId: askedBot.botId, answer: "trust" }); disableAll();
    }, `Answer "Yes, I trust this folder" for ${askedBot.nickname}`));
    node.appendChild(button("tf-sys__open", "Don't", () => {
      send({ type: "team.bot.answer", projectId: project.id, botId: askedBot.botId, answer: "exit" }); disableAll();
    }, `Answer "No, exit" for ${askedBot.nickname}`));
    const o = openBtn(askedBot);
    if (o) node.appendChild(o);
  }
  // Rows that need the owner IN the bot's terminal — it is asking something
  // the room can't relay, a typed post is sitting there unsent, or auto mode
  // blocked it — carry the door to it.
  // A post that never landed can be sent again from here: the same line into
  // the same bot, with no second post in the room. The row names the post
  // (note = its seq) and the bot (from), which is all the host needs.
  if (e.event === "undelivered" && e.note && target && project && /^\d+$/.test(e.note)) {
    node.appendChild(button("tf-sys__open tf-sys__open--primary", "Send again", () => {
      send({ type: "team.deliver.retry", projectId: project.id, seq: Number(e.note), botId: target.botId });
      disableAll();
    }, `Type it into ${target.nickname} again`));
  }
  if ((e.event === "waiting" || e.event === "permission" || e.event === "permission.expired"
       || e.event === "undelivered" || e.event === "denied") && target?.sessionId) {
    const o = openBtn(target);
    if (o) node.appendChild(o);
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
