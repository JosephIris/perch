// The Inspector rail: a third in-flow column that follows the focused pane and
// answers "what has this agent done and said?" without scrolling the terminal.
//
// ONE stream, not tabs. The agent's prose beats are the spine; its tool calls
// are dimmed connective tissue hung between them. That interleave is the whole
// point — "Found it, the LOC isn't miscounted" sitting directly under six reads
// of perch.log gives you the conclusion and its evidence in one glance, which
// separate tabs cannot. The ☰ button hides the work rows, which IS the
// prose-only view — a density toggle rather than a tab.
//
// Changes rides above the stream as a collapsible strip rather than a third
// surface, because it's STATE (what the tree looks like now), not an event, and
// it has no place in a chronological feed.
//
// Self-wiring, like initCloud(): owns its DOM, registers its own host listener,
// and main.ts just calls initInspector() once.

import { send, onMessage, type InspectorDataMessage, type InspectorEventView,
         type PaneTreeView, type StateMessage } from "./bridge.js";

// ---- Data layer ------------------------------------------------------------
// Same request/reply + cache shape as commits.ts. The cache exists so switching
// BACK to a pane is instant (no skeleton, no flash); a fresh request always goes
// out behind it, and re-renders when it lands.

const cache = new Map<string, InspectorDataMessage>();
type Pending = { resolve: (d: InspectorDataMessage) => void; timer: number };
const inflight = new Map<string, Pending>();

// The host always replies, but if it throws before posting we must not leave the
// rail stuck on a skeleton forever. Resolve empty instead.
const FETCH_TIMEOUT_MS = 5000;

// How often to re-read while the focused agent is actually doing something. The
// host tails the transcript by byte offset, so a poll costs only the rows
// appended since the last one — cheap enough to keep the rail live, slow enough
// not to thrash the DOM. We poll rather than ride `state` because an agent
// writing prose and reading files produces no state change at all: nothing in
// the snapshot moves, but the journal is growing the whole time.
const POLL_MS = 2000;

// Don't show a loading state that would only flash. The transcript read is
// ~10-30ms, so a skeleton painted immediately would appear and vanish inside a
// single frame and read as a glitch. Wait this long; if the data beats it, the
// user never sees a loading state at all — which is the point.
const SKELETON_DELAY_MS = 100;

function requestInspector(paneId: string): Promise<InspectorDataMessage> {
  const existing = inflight.get(paneId);
  if (existing) {
    // Coalesce: a poll landing on top of an in-flight request must not stack.
    return new Promise((r) => {
      const prev = existing.resolve;
      existing.resolve = (d) => { prev(d); r(d); };
    });
  }
  return new Promise<InspectorDataMessage>((resolve) => {
    const timer = window.setTimeout(() => {
      inflight.delete(paneId);
      resolve(empty(paneId));
    }, FETCH_TIMEOUT_MS);
    inflight.set(paneId, { resolve, timer });
    send({ type: "inspector.request", paneId });
  });
}

const empty = (paneId: string): InspectorDataMessage => ({
  type: "inspector.data", paneId, hasAgent: false,
  events: [], vitals: null, files: [], added: 0, deleted: 0,
});

onMessage((msg) => {
  if (msg.type !== "inspector.data") return;
  cache.set(msg.paneId, msg);
  const p = inflight.get(msg.paneId);
  if (!p) return;
  clearTimeout(p.timer);
  inflight.delete(msg.paneId);
  p.resolve(msg);
});

// ---- Element helpers -------------------------------------------------------

const el = (tag: string, cls?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  return e;
};
const elText = (tag: string, cls: string, text: string): HTMLElement => {
  const e = el(tag, cls);
  e.textContent = text;
  return e;
};

/** "+412 −89", omitting either side when it's zero. Shared diff palette. */
function appendDiff(host: HTMLElement, added: number, deleted: number): void {
  if (added) host.appendChild(elText("span", "diff-add", `+${added}`));
  if (deleted) {
    if (added) host.append(" ");
    host.appendChild(elText("span", "diff-del", `−${deleted}`));
  }
}

/** 12345 → "12.3k". The rail is 336px; six-digit token counts don't fit. */
function compact(n: number): string {
  if (n < 1000) return String(n);
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
  return `${(n / 1_000_000).toFixed(1)}M`;
}

/** Local wall-clock "19:32" — the journal is read against the terminal beside
 *  it, and the terminal shows local time. */
function hhmm(iso: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return "";
  const d = new Date(t);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

/** Render the two bits of inline markdown an agent actually uses in prose:
 *  **bold** and `code`. Left raw, they're pure noise — a beat reads as
 *  "**Pushed to global_dnn** (branch `user-cache-restrict`)", asterisks and all,
 *  which is worse than either rendering them or stripping them.
 *
 *  Tokenized into DOM nodes rather than assigned as innerHTML: transcript text
 *  is agent output, and agent output is not something we hand to an HTML parser.
 *  Anything more than these two forms (headings, lists, links) stays literal —
 *  a 336px rail is not a markdown viewer. */
function appendInline(host: HTMLElement, text: string): void {
  const re = /\*\*([^*]+)\*\*|`([^`]+)`/g;
  let last = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (m.index > last) host.append(text.slice(last, m.index));
    if (m[1] !== undefined) host.appendChild(elText("strong", "beat__strong", m[1]));
    else host.appendChild(elText("code", "beat__code", m[2]));
    last = m.index + m[0].length;
  }
  if (last < text.length) host.append(text.slice(last));
}

// ---- Rendering -------------------------------------------------------------

function renderChanges(host: HTMLElement, data: InspectorDataMessage, open: boolean): void {
  host.replaceChildren();
  if (!data.files.length) return;

  const bar = el("button", "changes__bar") as HTMLButtonElement;
  bar.type = "button";
  bar.setAttribute("aria-expanded", String(open));
  bar.appendChild(elText("span", "changes__caret", "▶"));
  bar.appendChild(elText("span", "changes__label",
    `${data.files.length} ${data.files.length === 1 ? "file" : "files"} changed`));
  const loc = el("span", "changes__loc");
  appendDiff(loc, data.added, data.deleted);
  bar.appendChild(loc);
  host.appendChild(bar);

  const body = el("div", "changes__body");
  body.hidden = !open;
  for (const f of data.files) {
    const row = el("div", "file-row");
    // Filename bright, directory dim. At 336px a full path ellipsizes into
    // uselessness, and the filename is what you actually scan for.
    const slash = f.path.lastIndexOf("/");
    row.appendChild(elText("span", "file-row__name",
      slash >= 0 ? f.path.slice(slash + 1) : f.path));
    row.appendChild(elText("span", "file-row__dir", slash >= 0 ? f.path.slice(0, slash) : ""));
    const rl = el("span", "file-row__loc");
    appendDiff(rl, f.added, f.deleted);
    row.appendChild(rl);
    row.title = f.path;
    body.appendChild(row);
  }
  host.appendChild(body);

  bar.addEventListener("click", () => {
    const nowOpen = bar.getAttribute("aria-expanded") !== "true";
    bar.setAttribute("aria-expanded", String(nowOpen));
    body.hidden = !nowOpen;
    changesOpen = nowOpen;
  });
}

function renderEvent(e: InspectorEventView, i: number): HTMLElement {
  if (e.kind === "interrupt") {
    // You hit Esc / Ctrl-C. Painted as an alarm — red row, "!" badge — so a
    // stopped turn is obvious at a glance instead of hiding as another prompt.
    // The brackets on "[Request interrupted …]" are dropped now that the badge
    // carries the meaning.
    const it = el("div", "turn-interrupt");
    it.appendChild(elText("span", "turn-interrupt__mark", "!"));
    it.appendChild(elText("span", "turn-interrupt__text", e.text.replace(/^\[|\]$/g, "")));
    return it;
  }

  if (e.kind === "prompt") {
    // Clamped like a beat. A pasted spec runs dozens of lines, and unclamped it
    // turns the prompt header into a wall that buries the turn hanging off it —
    // the same "can't scan it" failure the clamp on beats exists to prevent. Show
    // ~4 lines with a chevron to open it in place; expansion is keyed by index
    // (the event list is append-only) so it survives the poll re-render.
    const p = el("div", expanded.has(i) ? "turn-prompt turn-prompt--open" : "turn-prompt");
    p.dataset.i = String(i);
    p.appendChild(elText("span", "turn-prompt__caret", ">"));
    p.appendChild(elText("span", "turn-prompt__text", e.text));
    p.appendChild(el("span", "turn-prompt__chev"));
    p.addEventListener("click", () => {
      const open = p.classList.toggle("turn-prompt--open");
      if (open) expanded.add(i); else expanded.delete(i);
    });
    return p;
  }

  if (e.kind === "beat") {
    // Clamped to 4 lines, click to open. An agent's message can run 40+ lines,
    // and one of those unclamped buries every beat around it — the journal stops
    // being scannable and becomes the terminal again, which is the whole thing
    // we were escaping. Expansion is keyed by index (the event list is
    // append-only, so an index is stable) and survives the poll re-render.
    const b = el("div", expanded.has(i) ? "beat beat--open" : "beat");
    b.dataset.i = String(i);
    b.appendChild(elText("span", "beat__time", hhmm(e.ts)));
    const text = el("span", "beat__text");
    appendInline(text, e.text);
    b.appendChild(text);
    b.addEventListener("click", () => {
      const open = b.classList.toggle("beat--open");
      if (open) expanded.add(i); else expanded.delete(i);
    });
    return b;
  }

  if (e.kind === "skill") {
    // A skill invocation — coloured violet and glyph-marked so a packaged
    // capability reads apart from the dim tool-call texture around it.
    const s = el("div", "skill");
    s.appendChild(elText("span", "skill__time", hhmm(e.ts)));
    s.appendChild(elText("span", "skill__glyph", "◆"));
    const what = el("span", "skill__what");
    what.appendChild(elText("span", "skill__verb", "Skill"));
    if (e.target) what.appendChild(elText("span", "skill__target", e.target));
    s.appendChild(what);
    return s;
  }

  // work — deliberately quiet. It reads as texture, not content: your eye skips
  // it and lands on the beats, but it's there the moment you want to know what
  // actually happened between two of them.
  const w = el("div", e.repeat > 1 ? "work work--repeat" : "work");
  w.appendChild(elText("span", "work__time", hhmm(e.ts)));
  w.appendChild(elText("span", "work__rail", "│"));
  const what = el("span", "work__what");
  what.appendChild(elText("span", "work__verb", e.verb));
  if (e.target) what.appendChild(elText("span", "work__target", e.target));
  w.appendChild(what);
  if (e.repeat > 1) w.appendChild(elText("span", "work__repeat", `×${e.repeat}`));
  else if (e.note) w.appendChild(elText("span", "work__note", e.note));
  return w;
}

function renderStream(host: HTMLElement, data: InspectorDataMessage): void {
  host.replaceChildren();

  if (!data.events.length) {
    const e = el("div", "inspector__empty");
    e.appendChild(elText("div", "inspector__empty-title",
      data.hasAgent ? "Nothing yet" : "No agent in this pane"));
    e.appendChild(elText("div", "inspector__empty-body", data.hasAgent
      ? "The agent hasn't said anything yet."
      : "Start Claude here and its work shows up in this rail."));
    host.appendChild(e);
    return;
  }

  const frag = document.createDocumentFragment();
  data.events.forEach((ev, i) => frag.appendChild(renderEvent(ev, i)));
  host.appendChild(frag);

  // Only a beat/prompt that ACTUALLY overflows gets the expand affordance. We
  // can't know that before layout, so mark them after insertion — a short one
  // that showed a "there's more" chevron and then did nothing would be a small
  // lie.
  for (const b of host.querySelectorAll<HTMLElement>(".beat")) {
    const t = b.querySelector<HTMLElement>(".beat__text");
    if (!t) continue;
    b.classList.toggle("beat--clamped",
      !b.classList.contains("beat--open") && t.scrollHeight > t.clientHeight + 2);
  }
  for (const p of host.querySelectorAll<HTMLElement>(".turn-prompt")) {
    const t = p.querySelector<HTMLElement>(".turn-prompt__text");
    if (!t) continue;
    p.classList.toggle("turn-prompt--clamped",
      !p.classList.contains("turn-prompt--open") && t.scrollHeight > t.clientHeight + 2);
  }
}

function renderVitals(host: HTMLElement, data: InspectorDataMessage): void {
  host.replaceChildren();
  const v = data.vitals;
  if (!v) { host.hidden = true; return; }
  host.hidden = false;

  host.appendChild(elText("span", "vitals__model", v.model));

  // Cost is an API-EQUIVALENT estimate and is labelled as one: on a Claude
  // subscription there is no bill, only quota, so context headroom is the
  // number that actually matters and it leads. A model we have no published
  // rate for prices at 0 host-side — show nothing rather than invent a figure.
  if (v.costUsd > 0) {
    host.appendChild(elText("span", "vitals__sep", "·"));
    const cost = elText("span", "vitals__cost", `≈$${v.costUsd.toFixed(2)}`);
    cost.title = "Estimated API-equivalent cost. On a Claude subscription you aren't billed per token.";
    host.appendChild(cost);
  }

  host.appendChild(el("span", "vitals__spacer"));

  const pct = v.contextMax > 0
    ? Math.min(100, Math.round((v.contextTokens / v.contextMax) * 100))
    : 0;
  const ctx = el("span", "vitals__ctx");
  ctx.title = `Context: ${compact(v.contextTokens)} of ${compact(v.contextMax)} tokens`;
  const bar = el("span", "ctx-bar");
  const fill = el("span", "ctx-bar__fill");
  fill.style.width = `${pct}%`;
  bar.appendChild(fill);
  ctx.appendChild(bar);
  ctx.appendChild(elText("span", "vitals__pct", `${pct}%`));
  host.appendChild(ctx);
}

function renderSkeleton(host: HTMLElement): void {
  host.replaceChildren();
  const frag = document.createDocumentFragment();
  // Beat-shaped rows, not a spinner: the placeholder should look like what's
  // about to arrive, so the swap is a fill-in rather than a layout jump.
  for (let i = 0; i < 5; i++) {
    const s = el("div", "skeleton");
    s.appendChild(el("span", "skeleton__time"));
    const lines = el("span", "skeleton__lines");
    lines.appendChild(el("span", "skeleton__line"));
    if (i % 2 === 0) lines.appendChild(el("span", "skeleton__line skeleton__line--short"));
    s.appendChild(lines);
    frag.appendChild(s);
  }
  host.appendChild(frag);
}

// ---- Controller ------------------------------------------------------------

const NEAR_BOTTOM_PX = 24;

let paneId: string | null = null;
let paneName = "";
let changesOpen = false;
/** Indices of beats the user opened. Kept outside the render so a poll tick
 *  can't slam a message shut mid-read; cleared on pane change, since indices
 *  mean nothing across transcripts. */
const expanded = new Set<number>();
/** Which journal kinds are shown. Global (like the quiet toggle this replaces)
 *  and session-only. Interrupts ride with "user" — they're your action. Applied
 *  as CSS classes on the stream, so a toggle never re-renders or refetches. */
const shown = { user: true, claude: true, actions: true, skill: true };
type FilterCat = keyof typeof shown;
let pollTimer: number | null = null;
let skeletonTimer: number | null = null;

let appEl: HTMLElement;
let railEl: HTMLElement;
let tagEl: HTMLElement;
let nameEl: HTMLElement;
let changesEl: HTMLElement;
let streamEl: HTMLElement;
let vitalsEl: HTMLElement;
let jumpEl: HTMLButtonElement;
let allBtn: HTMLButtonElement;
const filterBtns: HTMLButtonElement[] = [];
const filterCounts: Record<string, HTMLElement> = {};

const isNearBottom = (e: HTMLElement) =>
  e.scrollHeight - e.scrollTop - e.clientHeight < NEAR_BOTTOM_PX;

function apply(data: InspectorDataMessage): void {
  // Stick to latest — but only if we were ALREADY at the bottom. New rows
  // arriving while the user is reading history must never yank the viewport;
  // that's the difference between "live" and "unusable".
  const pinned = isNearBottom(streamEl);
  const prevTop = streamEl.scrollTop;

  renderChanges(changesEl, data, changesOpen);
  renderStream(streamEl, data);
  updateFilterCounts();
  renderVitals(vitalsEl, data);

  if (pinned) streamEl.scrollTop = streamEl.scrollHeight;
  else streamEl.scrollTop = prevTop;
  jumpEl.classList.toggle("inspector__jump--on", !isNearBottom(streamEl));
}

/** Point the rail at a pane. `swap` is true on a focus change (content is about
 *  to be replaced wholesale) and false on a poll (same pane, more rows). */
function load(id: string, swap: boolean): void {
  if (skeletonTimer !== null) { clearTimeout(skeletonTimer); skeletonTimer = null; }

  const cached = cache.get(id);
  if (swap) {
    if (cached) {
      // Instant — switching back to a pane you've already looked at should
      // never show a loading state.
      apply(cached);
      streamEl.scrollTop = streamEl.scrollHeight;
    } else {
      // Delayed skeleton: if the reply beats the timer, this never fires and
      // the user sees no loading state at all. Crucially we do NOT blank the
      // stream first — a flash of empty is worse than a stale frame.
      skeletonTimer = window.setTimeout(() => {
        skeletonTimer = null;
        if (paneId === id) renderSkeleton(streamEl);
      }, SKELETON_DELAY_MS);
    }
  }

  requestInspector(id).then((data) => {
    if (paneId !== id) return;                   // focus moved on; drop the reply
    if (skeletonTimer !== null) { clearTimeout(skeletonTimer); skeletonTimer = null; }
    apply(data);
    if (swap) streamEl.scrollTop = streamEl.scrollHeight;
  });
}

function setPane(id: string | null, name: string, color: number, live: boolean): void {
  const changed = id !== paneId;
  paneId = id;

  if (changed) {
    expanded.clear();          // indices are per-transcript; they don't carry over
    paneName = name;
    nameEl.textContent = name;
    tagEl.style.background = `var(--color-pane-tag-${color % 6})`;
    tagEl.hidden = id === null;
    if (!id) {
      changesEl.replaceChildren();
      renderStream(streamEl, empty(""));
      updateFilterCounts();
      vitalsEl.hidden = true;
    } else {
      load(id, /* swap */ true);
    }
  } else if (name !== paneName) {
    paneName = name;
    nameEl.textContent = name;
  }

  // Poll only while the agent is actually working. An idle pane's transcript
  // isn't growing, so polling it would be pure waste.
  if (pollTimer !== null) { clearInterval(pollTimer); pollTimer = null; }
  if (id && live) {
    pollTimer = window.setInterval(() => {
      if (paneId) load(paneId, /* swap */ false);
    }, POLL_MS);
  }
}

function leaves(node: PaneTreeView): Extract<PaneTreeView, { kind: "leaf" }>[] {
  return node.kind === "leaf" ? [node] : node.children.flatMap(leaves);
}

function onState(msg: StateMessage): void {
  setOpen(msg.prefs?.inspectorOpen ?? true, /* persist */ false);

  const active = msg.activePaneId || null;
  if (!active) { setPane(null, "", 0, false); return; }

  for (const sess of msg.sessions) {
    for (const leaf of leaves(sess.rootPane)) {
      if (leaf.paneId !== active) continue;
      // "working" is the only state whose transcript is still growing. done /
      // waiting / permission are all settled — one last read lands on the state
      // push itself (setPane → load on change), and then we stop.
      const live = leaf.agentState === "working";
      setPane(leaf.paneId, leaf.name, leaf.colorIndex, live);
      // A settled pane still needs ONE fresh read to pick up the final beat
      // ("All 61 tests pass") that landed after our last poll.
      if (!live && leaf.paneId === paneId) load(leaf.paneId, /* swap */ false);
      return;
    }
  }
  setPane(null, "", 0, false);
}

// ---- Open / collapse -------------------------------------------------------

let open = true;

/** `persist` false when we're just reflecting the host's state back — otherwise
 *  every state push would write Settings.json again. */
function setOpen(next: boolean, persist: boolean): void {
  if (next === open && !persist) {
    appEl.classList.toggle("app--inspector-collapsed", !next);
    return;
  }
  open = next;
  // The pane container resizes when the column changes width, and each pane's
  // ResizeObserver fires xterm's fit addon — no explicit refit needed here
  // (same contract as the sidebar collapse).
  appEl.classList.toggle("app--inspector-collapsed", !open);
  railEl.setAttribute("aria-hidden", String(!open));
  if (persist) send({ type: "prefs.set", inspectorOpen: open });
}

export function toggleInspector(): void {
  setOpen(!open, /* persist */ true);
}

// ---- Filters ---------------------------------------------------------------

/** Reflect `shown` onto the stream: the hide classes drive the CSS, chip
 *  aria-pressed drives the visuals. "All" is pressed only when every kind is on;
 *  the stream gets an empty-filter marker when none is. Pure class toggling — no
 *  re-render — so it stays instant even mid-poll. */
function applyFilters(): void {
  streamEl.classList.toggle("inspector__stream--hide-user", !shown.user);
  streamEl.classList.toggle("inspector__stream--hide-claude", !shown.claude);
  streamEl.classList.toggle("inspector__stream--hide-actions", !shown.actions);
  streamEl.classList.toggle("inspector__stream--hide-skill", !shown.skill);
  for (const btn of filterBtns)
    btn.setAttribute("aria-pressed", String(shown[btn.dataset.cat as FilterCat]));
  const vals = Object.values(shown);
  allBtn.setAttribute("aria-pressed", String(vals.every(Boolean)));
  streamEl.classList.toggle("inspector__stream--empty-filter", !vals.some(Boolean));
}

/** Chip counts are TOTALS per kind (querySelectorAll matches hidden rows too),
 *  so they tell you what each filter would reveal, not what's showing now. */
function updateFilterCounts(): void {
  filterCounts.user.textContent =
    String(streamEl.querySelectorAll(".turn-prompt, .turn-interrupt").length);
  filterCounts.claude.textContent = String(streamEl.querySelectorAll(".beat").length);
  filterCounts.actions.textContent = String(streamEl.querySelectorAll(".work").length);
  filterCounts.skill.textContent = String(streamEl.querySelectorAll(".skill").length);
}

// ---- Init ------------------------------------------------------------------

export function initInspector(): void {
  const $ = <T extends HTMLElement>(id: string): T => {
    const e = document.getElementById(id);
    if (!e) throw new Error(`#${id} missing in index.html`);
    return e as T;
  };

  appEl = $("app");
  railEl = $("inspector");
  tagEl = $("inspector-tag");
  nameEl = $("inspector-name");
  changesEl = $("inspector-changes");
  streamEl = $("inspector-stream");
  vitalsEl = $("inspector-vitals");
  jumpEl = $<HTMLButtonElement>("inspector-jump");

  $("inspector-close").addEventListener("click", () => toggleInspector());

  // Journal filter chips. Each toggles its kind; "All" flips everything on, or
  // clears it when everything's already on. Toggling scrolls back to latest so
  // the newest visible row stays in view.
  allBtn = $<HTMLButtonElement>("filter-all");
  (["user", "claude", "actions", "skill"] as FilterCat[]).forEach((cat) => {
    const btn = $<HTMLButtonElement>(`filter-${cat}`);
    btn.addEventListener("click", () => {
      shown[cat] = !shown[cat];
      applyFilters();
      streamEl.scrollTop = streamEl.scrollHeight;
    });
    filterBtns.push(btn);
    filterCounts[cat] = $(`filter-count-${cat}`);
  });
  allBtn.addEventListener("click", () => {
    const target = !Object.values(shown).every(Boolean);
    for (const k of Object.keys(shown) as FilterCat[]) shown[k] = target;
    applyFilters();
    streamEl.scrollTop = streamEl.scrollHeight;
  });
  applyFilters();

  streamEl.addEventListener("scroll", () => {
    jumpEl.classList.toggle("inspector__jump--on", !isNearBottom(streamEl));
  });
  jumpEl.addEventListener("click", () => {
    streamEl.scrollTo({ top: streamEl.scrollHeight, behavior: "smooth" });
    jumpEl.classList.remove("inspector__jump--on");
  });

  onMessage((msg) => {
    if (msg.type === "state") onState(msg);
  });
}
