// The small pieces a project chat draws its threads with, shared by the
// conversation (chat-pane.ts) and the Overview beside it (chat-overview.ts):
// which group a thread is in and the one line that says what it is doing,
// its age, its progress ring, a thread named inline as a tag (with the card
// that opens when you hover it), and the handful of line icons both use.
//
// Pure functions first (tested in test/thread-ui.test.ts), DOM after.

import type { SessionView, AgentStateName } from "./bridge.js";

export type ThreadGroup = "waiting" | "ready" | "working" | "idle" | "resolved";

/** The Overview's groups, in the order they are shown: what needs you first. */
export const GROUPS: { id: ThreadGroup; label: string }[] = [
  { id: "waiting", label: "Waiting on you" },
  { id: "ready", label: "Ready for review" },
  { id: "working", label: "Working" },
  { id: "idle", label: "Idle" },
  { id: "resolved", label: "Resolved" },
];

const STATE_WORD: Record<AgentStateName, string> = {
  working: "Working", waiting: "Waiting for you", permission: "Needs your permission", done: "Finished its turn", idle: "Idle",
};

/** Which group a thread belongs in. The order of the checks is the order of
 *  what matters: done with → resolved; blocked on you → waiting; running →
 *  working; stopped with commits to merge → ready for review. */
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

/** The first line of a report, without Markdown's marks. */
export function firstLine(text: string | undefined): string {
  const plain = (text ?? "").replace(/\*\*|__|`/g, "").replace(/^#+\s*/gm, "");
  return plain.split("\n").map((l) => l.trim()).find((l) => l.length > 0) ?? "";
}

/** The grey line under a thread's title: what it is doing or has to say.
 *  `blocked` marks a permission prompt, drawn with a "Blocked" lead. */
export function statusLine(t: SessionView): { text: string; blocked: boolean } {
  const g = groupOf(t);
  const report = firstLine(t.threadReply);
  if (t.dormant && g !== "resolved") return { text: report || "Asleep", blocked: false };
  switch (g) {
    case "waiting":
      if (t.agentState === "permission")
        return { text: askText(t), blocked: true };
      return { text: t.notification?.text || report || "Waiting for you", blocked: false };
    case "working":
      return { text: t.threadTaskNow || t.activityDetail || "Working", blocked: false };
    case "ready":
      return { text: report || stateLabel(t), blocked: false };
    default:
      return { text: report || (g === "resolved" ? "Resolved" : "No report yet"), blocked: false };
  }
}

/** What a thread on a permission prompt wants: the tool call it is stopped
 *  on when Perch could read it, else what its notification says. */
export function askText(t: SessionView): string {
  return t.threadAsk || t.notification?.text || t.activityDetail || "Needs your permission";
}

/** When a thread last did something: its last report, its turn starting or
 *  ending — whichever is latest. 0 when it has done nothing yet. */
export function lastActiveMs(t: SessionView): number {
  return Math.max(t.threadReplyAtMs ?? 0, t.turnStartMs ?? 0, t.doneAtMs ?? 0);
}

/** A row's age: "now" under a minute, then one unit ("3m", "2h", "4d"). */
export function shortAge(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return "now";
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h`;
  return `${Math.floor(h / 24)}d`;
}

/** Within a group, the thread that moved last goes first; ties by number,
 *  newest first. */
export function byRecent(a: SessionView, b: SessionView): number {
  return lastActiveMs(b) - lastActiveMs(a) || (b.threadNumber ?? 0) - (a.threadNumber ?? 0);
}

/** "#3" in the coordinator's prose, where 3 is a thread of this chat: the
 *  pieces between and the numbers. Not "#3" inside a word, a URL fragment
 *  ("page#3") or an issue-like "PR #3"/"issue #3" the coordinator quotes. */
export function splitThreadRefs(text: string, known: (n: number) => boolean): (string | number)[] {
  const out: (string | number)[] = [];
  const rx = /(^|[^\w&#/])#(\d{1,4})\b/g;
  let at = 0;
  for (let m = rx.exec(text); m; m = rx.exec(text)) {
    const n = Number(m[2]);
    const before = text.slice(Math.max(0, m.index - 6), m.index + m[1].length);
    if (!known(n) || /\b(PR|pr|issue|Issue|ticket|bug)\s*$/.test(before)) continue;
    const start = m.index + m[1].length;
    if (start > at) out.push(text.slice(at, start));
    out.push(n);
    at = start + 1 + m[2].length;
  }
  if (at < text.length) out.push(text.slice(at));
  return out;
}

/** One quiet line for a run of tool calls: "Edited 2 files, ran 3 commands".
 *  Task-list updates are left out (the checklist shows them), so a run of
 *  only those says "". */
export function summarizeWork(steps: { verb: string; target: string }[]): string {
  const files = { edit: new Set<string>(), read: new Set<string>() };
  let ran = 0, searched = 0, web = 0, other = 0;
  for (const s of steps) {
    switch (s.verb) {
      case "Edit": case "MultiEdit": case "Write": case "NotebookEdit": files.edit.add(s.target); break;
      case "Read": files.read.add(s.target); break;
      case "Bash": case "PowerShell": ran++; break;
      case "Grep": case "Glob": case "LS": searched++; break;
      case "WebSearch": case "WebFetch": web++; break;
      case "TaskCreate": case "TaskUpdate": case "TaskList": case "TaskGet": case "TodoWrite": break;
      default: other++;
    }
  }
  const n = (k: number, one: string, many: string) => `${k} ${k === 1 ? one : many}`;
  const parts: string[] = [];
  if (files.edit.size) parts.push(`edited ${n(files.edit.size, "file", "files")}`);
  if (files.read.size) parts.push(`read ${n(files.read.size, "file", "files")}`);
  if (ran) parts.push(`ran ${n(ran, "command", "commands")}`);
  if (searched) parts.push(`searched ${n(searched, "time", "times")}`);
  if (web) parts.push(`looked at the web ${n(web, "time", "times")}`);
  if (other) parts.push(`used ${n(other, "other tool", "other tools")}`);
  const s = parts.join(", ");
  return s ? s[0].toUpperCase() + s.slice(1) : "";
}

/** A model id as a person says it: "claude-opus-5-5[1m]" → "Opus 5.5",
 *  "claude-haiku-4-5-20251001" → "Haiku 4.5". Unknown shapes pass through. */
export function modelLabel(id: string): string {
  const m = (id ?? "").toLowerCase().replace(/\[.*?\]/g, "").match(/^(?:claude-)?([a-z]+)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?$/);
  if (!m) return id ?? "";
  return `${m[1][0].toUpperCase()}${m[1].slice(1)} ${m[2]}${m[3] ? "." + m[3] : ""}`;
}

/** A divider's label for the day of `ms`: "Today", "Yesterday", else
 *  "Mon, Sep 22" (with the year when it isn't this one). */
export function dayLabel(ms: number, now = Date.now()): string {
  const d = new Date(ms), n = new Date(now);
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((day(n) - day(d)) / 86400000);
  if (diff === 0) return "Today";
  if (diff === 1) return "Yesterday";
  return d.toLocaleDateString(undefined, { weekday: "short", month: "short", day: "numeric", ...(d.getFullYear() !== n.getFullYear() ? { year: "numeric" } : {}) });
}

export const dayKey = (ms: number): string => { const d = new Date(ms); return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`; };

// ---- DOM ---------------------------------------------------------------------

export const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

export function button(cls: string, content: string | Node, onClick: () => void, title?: string): HTMLButtonElement {
  const b = el("button", cls) as HTMLButtonElement;
  b.type = "button";
  if (typeof content === "string") b.textContent = content; else b.appendChild(content);
  if (title) { b.title = title; b.setAttribute("aria-label", title); }
  b.addEventListener("click", (e) => { e.stopPropagation(); onClick(); });
  return b;
}

const SVG = "http://www.w3.org/2000/svg";

/** Line icons, 16px on a 16 grid, drawn in currentColor. */
const ICON_PATHS: Record<string, string> = {
  check: "M3.5 8.5l3 3 6-7",
  close: "M4 4l8 8M12 4l-8 8",
  chevron: "M4.5 6.5L8 10l3.5-3.5",
  crumb: "M6.5 4.5L10 8l-3.5 3.5",
  expand: "M9.5 3.5h3v3M12.5 3.5L8.5 7.5M6.5 12.5h-3v-3M3.5 12.5l4-4",
  enter: "M12.5 3.5v5a1.5 1.5 0 0 1-1.5 1.5H4M6.5 7.5L4 10l2.5 2.5",
  merge: "M5 5.5v5M5 5.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM5 13.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM11 7.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM11 7.5c0 2.5-6 1-6 3",
  list: "M6.5 4.5h6M6.5 8h6M6.5 11.5h6M3.5 4.5h.01M3.5 8h.01M3.5 11.5h.01",
  bubble: "M3 4.5A1.5 1.5 0 0 1 4.5 3h7A1.5 1.5 0 0 1 13 4.5v5a1.5 1.5 0 0 1-1.5 1.5H7l-3 2.5V11h.5A1.5 1.5 0 0 1 3 9.5z",
  hand: "M6 8V3.8a1 1 0 0 1 2 0V7.5M8 7V3a1 1 0 0 1 2 0v4.5M10 7.5V4.5a1 1 0 0 1 2 0V10a4 4 0 0 1-4 4h-.5a4 4 0 0 1-3.3-1.8L2.8 10a1 1 0 0 1 1.6-1.2L6 10.2",
  reply: "M6.5 4.5L3.5 7.5l3 3M3.5 7.5H10a2.5 2.5 0 0 1 2.5 2.5v1.5",
  copy: "M5.5 5.5V4a1.5 1.5 0 0 1 1.5-1.5h5A1.5 1.5 0 0 1 13.5 4v5a1.5 1.5 0 0 1-1.5 1.5h-1.5M3.5 5.5h5A1.5 1.5 0 0 1 10 7v5a1.5 1.5 0 0 1-1.5 1.5h-5A1.5 1.5 0 0 1 2 12V7a1.5 1.5 0 0 1 1.5-1.5z",
  stop: "M5 5h6v6H5z",
  gear: "M6.9 1.8h2.2l.35 1.6 1.2.7 1.55-.5 1.1 1.9-1.2 1.1v1.4l1.2 1.1-1.1 1.9-1.55-.5-1.2.7-.35 1.6H6.9l-.35-1.6-1.2-.7-1.55.5-1.1-1.9 1.2-1.1V7.3L2.7 6.2l1.1-1.9 1.55.5 1.2-.7zM8 10a2 2 0 1 0 0-4 2 2 0 0 0 0 4z",
};

export function icon(name: keyof typeof ICON_PATHS | string, cls = "pc-icon"): SVGSVGElement {
  const svg = document.createElementNS(SVG, "svg");
  svg.setAttribute("viewBox", "0 0 16 16");
  svg.setAttribute("width", "16");
  svg.setAttribute("height", "16");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("class", cls);
  const p = document.createElementNS(SVG, "path");
  p.setAttribute("d", ICON_PATHS[name] ?? "");
  p.setAttribute("fill", "none");
  p.setAttribute("stroke", "currentColor");
  p.setAttribute("stroke-width", "1.3");
  p.setAttribute("stroke-linecap", "round");
  p.setAttribute("stroke-linejoin", "round");
  svg.appendChild(p);
  return svg;
}

const RING_R = 5.25;
const RING_C = 2 * Math.PI * RING_R;

/** A 14px progress ring: `done` of `total` as an arc. With no total it is
 *  a spinner (a thread working without a task list). Update with setRing. */
export function ring(): SVGSVGElement {
  const svg = document.createElementNS(SVG, "svg");
  svg.setAttribute("viewBox", "0 0 14 14");
  svg.setAttribute("width", "14");
  svg.setAttribute("height", "14");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("class", "pc-ring");
  for (const cls of ["pc-ring__track", "pc-ring__arc"]) {
    const c = document.createElementNS(SVG, "circle");
    c.setAttribute("cx", "7"); c.setAttribute("cy", "7"); c.setAttribute("r", String(RING_R));
    c.setAttribute("class", cls);
    c.setAttribute("fill", "none");
    svg.appendChild(c);
  }
  const arc = svg.lastElementChild as SVGCircleElement;
  arc.style.strokeDasharray = String(RING_C);
  arc.style.strokeDashoffset = String(RING_C);
  return svg;
}

export function setRing(svg: SVGSVGElement, done: number, total: number) {
  const arc = svg.lastElementChild as SVGCircleElement;
  const spin = total <= 0;
  svg.classList.toggle("pc-ring--spin", spin);
  // A spinner shows a quarter arc; progress shows at least a sliver so 0/3
  // still reads as "started".
  const frac = spin ? 0.25 : Math.max(0.08, Math.min(1, done / total));
  arc.style.strokeDashoffset = String(RING_C * (1 - frac));
}

/** Live "now / 3m" labels: one shared ticker rewrites every [data-pc-age]. */
let ageTicker = 0;
export function ageLabel(atMs: number, cls = "pc-age"): HTMLElement {
  const e = el("span", cls);
  setAge(e, atMs);
  if (!ageTicker) ageTicker = window.setInterval(() => {
    const now = Date.now();
    document.querySelectorAll<HTMLElement>("[data-pc-age]").forEach((n) => {
      const at = Number(n.dataset.pcAge) || 0;
      const next = at > 0 ? shortAge(now - at) : "";
      if (n.textContent !== next) n.textContent = next;
    });
  }, 15000);
  return e;
}

export function setAge(e: HTMLElement, atMs: number) {
  e.dataset.pcAge = String(atMs);
  e.textContent = atMs > 0 ? shortAge(Date.now() - atMs) : "";
}

// ---- a thread named inline, and its hover card -------------------------------

/** A thread named in the conversation: a tag with its state dot and title.
 *  `lookup` is asked on every hover and redraw, so the tag follows the thread
 *  as it moves on; a thread that is gone keeps the title it had. */
export function threadChip(id: string, fallbackTitle: string, lookup: (id: string) => SessionView | undefined, open: (id: string) => void): HTMLElement {
  const chip = el("span", "pc-chip");
  chip.dataset.threadId = id;
  chip.dataset.title = fallbackTitle;
  chip.tabIndex = 0;
  chip.setAttribute("role", "link");
  fillChip(chip, lookup(id));
  chip.addEventListener("click", (e) => { e.stopPropagation(); hidePop(true); open(id); });
  chip.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); open(id); } });
  chip.addEventListener("mouseenter", () => schedulePop(chip, lookup));
  chip.addEventListener("mouseleave", () => hidePopSoon());
  chip.addEventListener("focus", () => schedulePop(chip, lookup));
  chip.addEventListener("blur", () => hidePopSoon());
  return chip;
}

export function fillChip(chip: HTMLElement, t: SessionView | undefined) {
  const title = t?.title ?? chip.dataset.title ?? "";
  chip.dataset.group = t ? groupOf(t) : "resolved";
  if (chip.dataset.shown === title && chip.childElementCount) return;
  chip.dataset.shown = title;
  chip.replaceChildren(el("span", "pc-dot"), el("span", "pc-chip__text", title));
}

let pop: HTMLElement | null = null;
let popFor: HTMLElement | null = null;
let popTimer = 0;
let hideTimer = 0;

function schedulePop(chip: HTMLElement, lookup: (id: string) => SessionView | undefined) {
  window.clearTimeout(hideTimer);
  window.clearTimeout(popTimer);
  if (popFor === chip && pop?.classList.contains("pc-pop--open")) return;
  // Moving from one tag to the next while a card is open: swap at once.
  const delay = pop?.classList.contains("pc-pop--open") ? 0 : 350;
  popTimer = window.setTimeout(() => showPop(chip, lookup), delay);
}

function hidePopSoon() {
  window.clearTimeout(popTimer);
  window.clearTimeout(hideTimer);
  hideTimer = window.setTimeout(() => hidePop(false), 140);
}

export function hidePop(now: boolean) {
  window.clearTimeout(popTimer);
  window.clearTimeout(hideTimer);
  if (!pop) return;
  pop.classList.remove("pc-pop--open");
  popFor = null;
  if (now) pop.style.visibility = "hidden";
}

function showPop(chip: HTMLElement, lookup: (id: string) => SessionView | undefined) {
  if (!chip.isConnected) return;
  if (!pop) {
    pop = el("div", "pc-pop");
    pop.setAttribute("role", "tooltip");
    pop.addEventListener("mouseenter", () => window.clearTimeout(hideTimer));
    pop.addEventListener("mouseleave", () => hidePopSoon());
    document.body.appendChild(pop);
  }
  const t = lookup(chip.dataset.threadId ?? "");
  const head = el("div", "pc-pop__head");
  const state = el("span", "pc-pop__state");
  state.dataset.group = t ? groupOf(t) : "resolved";
  state.append(el("span", "pc-dot"), el("span", undefined, t ? stateLabel(t) : "Closed"));
  head.appendChild(state);
  const meta: string[] = [];
  if (t && (t.threadTasksTotal ?? 0) > 0) meta.push(`${t.threadTasksDone}/${t.threadTasksTotal} tasks`);
  if (t && lastActiveMs(t) > 0) meta.push(shortAge(Date.now() - lastActiveMs(t)));
  head.appendChild(el("span", "pc-pop__meta", meta.join(" · ")));
  const title = el("div", "pc-pop__title", t?.title ?? chip.dataset.title ?? "");
  pop.replaceChildren(head, title);
  if (t) {
    const s = statusLine(t);
    if (s.text && s.text !== stateLabel(t)) pop.appendChild(el("div", "pc-pop__line", s.text));
  }
  // Below the tag, left edges aligned, kept on screen.
  pop.style.visibility = "hidden";
  pop.classList.remove("pc-pop--open");
  pop.style.left = "0px";
  pop.style.top = "0px";
  const r = chip.getBoundingClientRect();
  const w = pop.offsetWidth, h = pop.offsetHeight;
  const left = Math.max(8, Math.min(r.left, window.innerWidth - w - 8));
  const below = r.bottom + 6;
  const top = below + h > window.innerHeight - 8 ? Math.max(8, r.top - h - 6) : below;
  pop.style.left = `${Math.round(left)}px`;
  pop.style.top = `${Math.round(top)}px`;
  pop.style.visibility = "";
  popFor = chip;
  // Next frame, so the entry transition runs from the closed state.
  requestAnimationFrame(() => pop?.classList.add("pc-pop--open"));
}

/** Motion is decoration: skip it for people who asked the OS for less. */
export const reducedMotion = (): boolean =>
  typeof window !== "undefined" && !!window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
