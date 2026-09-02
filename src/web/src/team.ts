// The team room's data layer and every rendering decision it makes, as pure
// functions. No DOM here: team-room.ts owns the elements and asks this module
// what to show.
//
// Data flows the same way as the inspector's journal — request/reply against
// the host with a per-project cache, coalesced in-flight requests, and a
// timeout that resolves to the last known good payload rather than an empty
// one. The one difference: the host also PUSHES team.data while the room is
// open, so the merge is keyed by ledger seq and a push and a poll landing
// together can't duplicate a row.

import {
  send, onMessage,
  type AgentStateName, type ProjectView, type SessionView, type StateMessage,
  type TeamBotView, type TeamDataMessage, type TeamEntryView,
} from "./bridge.js";

// ---- Data layer ------------------------------------------------------------

const cache = new Map<string, TeamDataMessage>();
type Pending = { resolve: (d: TeamDataMessage) => void; timer: number };
const inflight = new Map<string, Pending>();
type Subscriber = (d: TeamDataMessage) => void;
const subscribers = new Map<string, Set<Subscriber>>();

// The host always replies, but if it throws before posting we must not leave
// the room on a skeleton forever. Resolve with what we last knew.
const FETCH_TIMEOUT_MS = 5000;

/** The merged ledger we hold for a project, if we've ever had one. */
export function cachedTeam(projectId: string): TeamDataMessage | undefined {
  return cache.get(projectId);
}

/** Ask the host for the ledger. With `sinceSeq` only newer rows come back and
 *  are merged into the cache; without it the host sends its newest window. */
export function requestTeam(projectId: string, sinceSeq?: number): Promise<TeamDataMessage> {
  const existing = inflight.get(projectId);
  if (existing) {
    // Coalesce: a poll landing on top of an in-flight request must not stack.
    return new Promise((r) => {
      const prev = existing.resolve;
      existing.resolve = (d) => { prev(d); r(d); };
    });
  }
  return new Promise<TeamDataMessage>((resolve) => {
    const timer = setTimeout(() => {
      inflight.delete(projectId);
      // Last known good beats a confident wrong "nothing here" — see
      // inspector.ts for the same rule and the bug that taught it.
      resolve(cache.get(projectId) ?? unknown(projectId));
    }, FETCH_TIMEOUT_MS);
    inflight.set(projectId, { resolve, timer });
    send(sinceSeq === undefined
      ? { type: "team.request", projectId }
      : { type: "team.request", projectId, sinceSeq });
  });
}

/** Nothing is known about this project's room YET. Distinct from a real reply
 *  with no entries, which means the room is genuinely quiet. */
function unknown(projectId: string): TeamDataMessage {
  return { type: "team.data", projectId, entries: [], lastSeq: 0, pending: true };
}

/** Be told whenever the merged ledger for a project changes (a reply landed or
 *  the host pushed). Returns the unsubscribe. */
export function subscribeTeam(projectId: string, cb: Subscriber): () => void {
  let set = subscribers.get(projectId);
  if (!set) { set = new Set(); subscribers.set(projectId, set); }
  set.add(cb);
  return () => { set!.delete(cb); };
}

/** Fold a team.data payload into the cache, settle any in-flight request for
 *  it, and notify subscribers. Exported so a harness can feed fixtures in. */
export function ingestTeamData(msg: TeamDataMessage): TeamDataMessage {
  const prior = cache.get(msg.projectId);
  const merged: TeamDataMessage = {
    type: "team.data",
    projectId: msg.projectId,
    entries: mergeEntries(prior?.entries ?? [], msg.entries),
    lastSeq: Math.max(prior?.lastSeq ?? 0, msg.lastSeq),
  };
  const truncated = msg.truncated ?? prior?.truncated;
  if (truncated) merged.truncated = true;
  cache.set(msg.projectId, merged);
  const p = inflight.get(msg.projectId);
  if (p) {
    clearTimeout(p.timer);
    inflight.delete(msg.projectId);
    p.resolve(merged);
  }
  const subs = subscribers.get(msg.projectId);
  if (subs) for (const cb of subs) cb(merged);
  return merged;
}

onMessage((msg) => {
  if (msg.type === "team.data") ingestTeamData(msg);
});

// ---- Pure: the ledger ------------------------------------------------------

/** Union of two entry lists by seq, ascending. An incoming row replaces a held
 *  one with the same seq (that's how a `delivered` flag flips), so the host can
 *  re-send a row to update it. */
export function mergeEntries(existing: TeamEntryView[], incoming: TeamEntryView[]): TeamEntryView[] {
  const bySeq = new Map<number, TeamEntryView>();
  for (const e of existing) bySeq.set(e.seq, e);
  for (const e of incoming) bySeq.set(e.seq, e);
  return [...bySeq.values()].sort((a, b) => a.seq - b.seq);
}

/** A rendered row: one ledger entry, or a run of a bot's tool calls folded into
 *  a single quiet line. `cont` (set by groupRows) marks a message that continues
 *  the previous one from the same author, so it drops its avatar and header. */
export type FeedRow =
  | { kind: "entry"; seq: number; entry: TeamEntryView; cont: boolean }
  | {
      kind: "workfold";
      seq: number;
      from: string;
      botId?: string;
      paneId?: string;
      entries: TeamEntryView[];
      summary: string;
      cont: boolean;
    };

/** Turn the ledger into rows. Consecutive `work` entries from one bot fold into
 *  one row; anything else — a beat, a system line, another bot's work — ends
 *  the run. The room is a conversation, and six file reads are one aside in
 *  it, not six messages. */
export function foldFeed(entries: TeamEntryView[]): FeedRow[] {
  const rows: FeedRow[] = [];
  let run: TeamEntryView[] = [];
  const flush = () => {
    if (run.length === 0) return;
    const first = run[0];
    const row: FeedRow = {
      kind: "workfold", seq: first.seq, from: first.from, entries: run,
      summary: summarizeWork(run), cont: false,
    };
    if (first.botId) row.botId = first.botId;
    if (first.paneId) row.paneId = first.paneId;
    rows.push(row);
    run = [];
  };
  for (const e of entries) {
    if (e.kind === "work") {
      if (run.length > 0 && run[0].from !== e.from) flush();
      run.push(e);
      continue;
    }
    flush();
    rows.push({ kind: "entry", seq: e.seq, entry: e, cont: false });
  }
  flush();
  return rows;
}

const EDIT_VERBS = new Set(["edit", "multiedit", "write", "notebookedit"]);
const READ_VERBS = new Set(["read", "glob", "grep"]);
const RUN_VERBS = new Set(["bash", "powershell", "monitor"]);

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** One line for a run of tool calls: "edited 3 files · read 6 · ran 2
 *  commands". Edits and reads count distinct files (six reads of one log are
 *  one file, which is the thrash signal the journal shows as ×6); commands and
 *  skills count calls, repeats included. */
export function summarizeWork(entries: TeamEntryView[]): string {
  const edited = new Set<string>();
  const read = new Set<string>();
  let ran = 0, skills = 0, other = 0;
  for (const e of entries) {
    const verb = (e.verb ?? "").toLowerCase();
    const n = Math.max(1, e.repeat ?? 1);
    const target = e.target ?? "";
    if (EDIT_VERBS.has(verb)) edited.add(target);
    else if (READ_VERBS.has(verb)) read.add(target);
    else if (RUN_VERBS.has(verb)) ran += n;
    else if (verb === "skill") skills += n;
    else other += n;
  }
  const parts: string[] = [];
  if (edited.size > 0) parts.push(`edited ${plural(edited.size, "file", "files")}`);
  if (read.size > 0) parts.push(`read ${plural(read.size, "file", "files")}`);
  if (ran > 0) parts.push(`ran ${plural(ran, "command", "commands")}`);
  if (skills > 0) parts.push(`used ${plural(skills, "skill", "skills")}`);
  if (other > 0) parts.push(plural(other, "other step", "other steps"));
  return parts.length > 0 ? parts.join(" · ") : "worked";
}

/* A message within this long of the previous one by the same author, of the
 * same kind, reads as a continuation — chat convention, and the visual grouping
 * the eye expects. */
const CONTINUATION_MS = 3 * 60 * 1000;
const MESSAGE_KINDS = new Set(["beat", "user", "peer", "note"]);

/** Mark continuation rows. Only message-like rows continue each other (a work
 *  fold or a system line always starts fresh), and a peer message to a
 *  different bot is a new header even from the same author. Returns new row
 *  objects; the input is left alone. */
export function groupRows(rows: FeedRow[]): FeedRow[] {
  const out: FeedRow[] = [];
  let prev: TeamEntryView | null = null;
  for (const row of rows) {
    if (row.kind !== "entry" || !MESSAGE_KINDS.has(row.entry.kind)) {
      out.push({ ...row, cont: false });
      prev = null;
      continue;
    }
    const e = row.entry;
    const cont =
      prev !== null &&
      prev.from === e.from &&
      prev.kind === e.kind &&
      sameRecipients(prev.to, e.to) &&
      within(prev.ts, e.ts, CONTINUATION_MS);
    out.push({ ...row, cont });
    prev = e;
  }
  return out;
}

function sameRecipients(a: TeamEntryView["to"], b: TeamEntryView["to"]): boolean {
  if (a === b) return true;
  if (!Array.isArray(a) || !Array.isArray(b)) return false;
  return a.length === b.length && a.every((x, i) => x === b[i]);
}

function within(a: string, b: string, ms: number): boolean {
  const ta = Date.parse(a), tb = Date.parse(b);
  if (Number.isNaN(ta) || Number.isNaN(tb)) return false;
  return Math.abs(tb - ta) <= ms;
}

/** Rows you'd want to know about since you last looked: messages, not tool
 *  calls, and never your own. */
export function unreadCount(entries: TeamEntryView[], lastSeenSeq: number): number {
  let n = 0;
  for (const e of entries) {
    if (e.seq <= lastSeenSeq) continue;
    if (e.kind === "work" || e.from === "you") continue;
    n++;
  }
  return n;
}

// ---- Pure: presence --------------------------------------------------------

/** What a bot is doing right now, in the sidebar's own vocabulary plus two the
 *  sidebar expresses structurally (dormant = filed under Idle; offline = no
 *  tab at all). `word` is what the roster prints next to the dot. */
export type Presence = { state: AgentStateName | "dormant" | "offline"; word: string };

export function presenceOf(session: SessionView | undefined): Presence {
  if (!session) return { state: "offline", word: "not running" };
  if (session.dormant) return { state: "dormant", word: "asleep" };
  switch (session.agentState) {
    case "working": return { state: "working", word: "working" };
    case "permission": return { state: "permission", word: "needs permission" };
    case "waiting": return { state: "waiting", word: "waiting for you" };
    case "done": return { state: "done", word: "idle" };
    default: return { state: "idle", word: "idle" };
  }
}

function sessionOf(bot: TeamBotView, sessions: SessionView[]): SessionView | undefined {
  if (!bot.sessionId) return undefined;
  return sessions.find((s) => s.id === bot.sessionId);
}

/* Loud first: what needs you, then what's moving, then what's resting. */
function rank(p: Presence): number {
  switch (p.state) {
    case "permission":
    case "waiting": return 0;
    case "working": return 1;
    case "done":
    case "idle": return 2;
    case "dormant": return 3;
    default: return 4;
  }
}

/** Roster order. Stable within a rank so bots don't shuffle on every poll. */
export function rosterSort(bots: TeamBotView[], sessions: SessionView[]): TeamBotView[] {
  return bots
    .map((b, i) => ({ b, i, r: rank(presenceOf(sessionOf(b, sessions))) }))
    .sort((x, y) => x.r - y.r || x.i - y.i)
    .map((x) => x.b);
}

/** Poll gate: the ledger only grows while some bot is working. */
export function anyWorking(bots: TeamBotView[], sessions: SessionView[]): boolean {
  return bots.some((b) => presenceOf(sessionOf(b, sessions)).state === "working");
}

/** The sidebar row's one line: "2 working · 1 waiting". Idle-only teams say
 *  so; an empty team says nothing. */
export function teamSummary(bots: TeamBotView[], sessions: SessionView[]): string {
  if (bots.length === 0) return "";
  let working = 0, needsYou = 0, idle = 0, asleep = 0, offline = 0;
  for (const b of bots) {
    const p = presenceOf(sessionOf(b, sessions));
    if (p.state === "working") working++;
    else if (p.state === "waiting" || p.state === "permission") needsYou++;
    else if (p.state === "dormant") asleep++;
    else if (p.state === "offline") offline++;
    else idle++;
  }
  const parts: string[] = [];
  if (needsYou > 0) parts.push(`${needsYou} waiting`);
  if (working > 0) parts.push(`${working} working`);
  if (parts.length === 0) {
    if (idle > 0) parts.push(`${idle} idle`);
    if (asleep > 0) parts.push(`${asleep} asleep`);
    if (offline > 0) parts.push(`${offline} not running`);
  }
  return parts.join(" · ");
}

// ---- Pure: the room's states -----------------------------------------------

/** The message the room shows instead of a feed, or null when there is a feed
 *  to show. Three cases that must not collapse into one: no team, a payload
 *  we're still waiting on, and a real reply that is simply empty. */
export function roomEmptyState(a: {
  bots: TeamBotView[];
  entries: TeamEntryView[];
  pending?: boolean;
}): { title: string; body: string } | null {
  if (a.bots.length === 0) {
    return { title: "No bots yet", body: "Add a bot to open the room." };
  }
  if (a.entries.length > 0) return null;
  if (a.pending) return { title: "Reading…", body: "Fetching the room." };
  return { title: "Nothing yet", body: "Say hello — @mention a bot, or write to everyone." };
}

/** Which project's room Ctrl+Shift+M opens: the active tab's project if it has
 *  bots, else the first project that does, else none. */
export function teamProjectFor(state: StateMessage): string | null {
  const hasBots = (p: ProjectView) => (p.team?.bots.length ?? 0) > 0;
  const active = state.sessions.find((s) => s.id === state.activeSessionId);
  if (active) {
    const p = state.projects.find((x) => x.id === active.projectId);
    if (p && hasBots(p)) return p.id;
  }
  const first = state.projects.find(hasBots);
  return first ? first.id : null;
}
