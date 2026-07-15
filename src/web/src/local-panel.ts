/* Local dev-servers panel.
 *
 * Sibling of the cloud panel, one axis over: cloud shows remote machines that
 * bill you; this shows servers listening on loopback on THIS machine — the dev
 * server you started, and (the whole point) the one that outlived the pane that
 * spawned it.
 *
 * Hierarchy is PORT → owner. The port is what you scan for and what you open, so
 * it leads; the pane that owns it explains it on line two. Three buckets, and
 * they differ in SHAPE, not just color: lingering is a contained caution box,
 * live and other are bare lists. Knock the yellow out and you can still tell the
 * "act on me" set apart.
 */
import { send, onMessage, type LocalResourceView, type LocalDataMessage } from "./bridge";
import { confirmDialog } from "./confirm";

let overlay: HTMLElement | null = null;
let latest: LocalResourceView[] = [];
/** Pids the user asked to kill — greyed until the next scan drops them. */
const deleting = new Set<number>();
let tick: number | null = null;
/** "Perch only" filter: when on, the "other" bucket (loopback listeners Perch
 *  never launched) is dropped from counts and the list. Mirrored from the host
 *  pref on every state push and persisted back on toggle. */
let perchOnly = false;
/** Per-row refreshers for the wall-clock text (uptime, "closed Xm ago"), rebuilt
 *  on every structural render. The 1s heartbeat runs these to keep those strings
 *  live WITHOUT replacing the list DOM — a full rebuild each second dropped
 *  :hover for a frame, which is what made the hovered row's highlight blink. */
let liveUpdaters: Array<() => void> = [];

/** The servers that count toward the panel + sidebar card. */
function visibleServers(): LocalResourceView[] {
  return perchOnly ? latest.filter((s) => s.kind !== "other") : latest;
}

// ---------------------------------------------------------------- formatting

function uptime(startedMs: number): string {
  if (!startedMs) return "—";
  const h = (Date.now() - startedMs) / 3_600_000;
  if (h <= 0) return "—";
  if (h < 1) return `${Math.max(1, Math.round(h * 60))}m`;
  if (h < 24) {
    const hh = Math.floor(h);
    const mm = Math.round((h - hh) * 60);
    return mm ? `${hh}h ${mm}m` : `${hh}h`;
  }
  const d = Math.floor(h / 24);
  const hh = Math.round(h - d * 24);
  return hh ? `${d}d ${hh}h` : `${d}d`;
}

/** "20m ago" / "just now" for when a lingering server lost its pane. */
function ago(ms?: number | null): string {
  if (!ms) return "recently";
  const s = (Date.now() - ms) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86400) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86400)}d ago`;
}

// ---------------------------------------------------------------- rendering

function el(tag: string, cls?: string, text?: string): HTMLElement {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

const OPEN_SVG =
  '<svg width="11" height="11" viewBox="0 0 12 12" fill="none" stroke="currentColor" ' +
  'stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round">' +
  '<path d="M4.5 2.5H2.5v7h7v-2"/><path d="M7 2.5h2.5V5"/><path d="M9.3 2.7 5.5 6.5"/></svg>';
const KILL_SVG =
  '<svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="currentColor" ' +
  'stroke-width="1.4" stroke-linecap="round"><path d="M2.5 2.5l7 7M9.5 2.5l-7 7"/></svg>';

/** The "where" line, per bucket. */
function whereFor(s: LocalResourceView): { where: string; state: string; dot: string } {
  if (s.kind === "lingering") {
    const who = s.paneName ? `“${s.paneName}” closed` : "pane closed";
    return { where: `${who} ${ago(s.closedMs)}`, state: "", dot: "linger" };
  }
  if (s.kind === "other") {
    const state = s.addr && s.addr !== "127.0.0.1" ? `· ${s.addr}` : "";
    return { where: "started outside Perch", state, dot: "other" };
  }
  // live
  return {
    where: s.paneName || "a pane",
    state: s.agentState ? `· ${s.agentState}` : "",
    dot: s.agentState ?? "idle",
  };
}

function renderServer(s: LocalResourceView): HTMLElement {
  const card = el("div", "lsrv" + (s.kind === "lingering" ? " lsrv--linger" : ""));
  if (deleting.has(s.pid)) card.classList.add("lsrv--deleting");

  // ---- line 1: the port ----
  const top = el("div", "lsrv__top");
  top.appendChild(el("span", "ltag", s.framework));
  top.appendChild(el("span", "lsrv__port", `:${s.port}`));
  top.appendChild(el("span", "lsrv__cmd", s.command));
  const spec = el("span", "lsrv__spec", `pid ${s.pid} · ${uptime(s.startedMs)}`);
  top.appendChild(spec);

  const open = el("button", "lsrv__act lsrv__open") as HTMLButtonElement;
  open.innerHTML = OPEN_SVG;
  open.setAttribute("aria-label", `Open localhost:${s.port}`);
  open.title = `Open http://localhost:${s.port}`;
  open.addEventListener("click", () => send({ type: "local.open", port: s.port }));
  top.appendChild(open);

  const kill = el("button", "lsrv__act lsrv__kill") as HTMLButtonElement;
  kill.innerHTML = KILL_SVG;
  kill.setAttribute("aria-label", `Kill pid ${s.pid}`);
  kill.title = `Kill pid ${s.pid}`;
  kill.disabled = deleting.has(s.pid);
  kill.addEventListener("click", () => void confirmKill(s));
  top.appendChild(kill);
  card.appendChild(top);

  // ---- line 2: who owns it ----
  const why = el("div", "lsrv__why");
  const { where, state, dot } = whereFor(s);
  const wdot = el("span", "lsrv__wdot");
  wdot.dataset.state = dot;
  why.appendChild(wdot);
  const whereEl = el("span", "lsrv__where", where);
  why.appendChild(whereEl);
  if (state) why.appendChild(el("span", "lsrv__state", state));
  card.appendChild(why);

  // Wall-clock text refreshed in place by the 1s heartbeat (see liveUpdaters):
  // uptime for every row, plus a lingering row's "closed Xm ago".
  liveUpdaters.push(() => {
    spec.textContent = `pid ${s.pid} · ${uptime(s.startedMs)}`;
    if (s.kind === "lingering") whereEl.textContent = whereFor(s).where;
  });
  return card;
}

function areaHead(
  title: string,
  count: number,
  opts: { linger?: boolean; onKillAll?: () => void; meta?: string },
): HTMLElement {
  const head = el("div", "larea__head");
  if (opts.linger) head.appendChild(el("span", "larea__dot"));
  head.appendChild(el("span", "larea__title", title));

  const meta = el("span", "larea__meta");
  meta.appendChild(document.createTextNode(`${count} server${count === 1 ? "" : "s"}`));
  if (opts.meta) meta.appendChild(document.createTextNode(` · ${opts.meta}`));
  head.appendChild(meta);

  if (opts.onKillAll) {
    head.appendChild(el("span", "larea__spacer"));
    const btn = el("button", "lbtn", "Kill all") as HTMLButtonElement;
    btn.addEventListener("click", opts.onKillAll);
    head.appendChild(btn);
  }
  return head;
}

function renderBody(): HTMLElement {
  liveUpdaters = [];
  const body = el("div", "lpanel__body");

  const vis = visibleServers();
  if (vis.length === 0) {
    // Tell the two empties apart: genuinely nothing listening, vs. everything
    // filtered out because it started outside Perch (with the way back).
    const otherHidden = perchOnly && latest.some((s) => s.kind === "other");
    const empty = el("div", "lpanel__empty");
    empty.appendChild(el("div", "lpanel__empty-title",
      otherHidden ? "Nothing you started here" : "Nothing listening"));
    empty.appendChild(el("div", "lpanel__empty-note", otherHidden
      ? `${latest.length} server${latest.length === 1 ? "" : "s"} listening, but started outside Perch. ` +
        `Turn off “Perch only” to see ${latest.length === 1 ? "it" : "them"}.`
      : "Dev servers you start in a pane show up here — with a link to open them and a button to kill any that outlive their tab.",
    ));
    body.appendChild(empty);
    return body;
  }

  const linger = vis.filter((s) => s.kind === "lingering");
  const live = vis.filter((s) => s.kind === "live");
  const other = vis.filter((s) => s.kind === "other");

  if (linger.length) {
    const area = el("div", "larea larea--linger");
    const head = areaHead("Still listening, no pane", linger.length, {
      linger: true,
      meta: `held since ${ago(Math.max(...linger.map((s) => s.closedMs ?? 0)))}`,
      onKillAll: () => void confirmKillLingering(linger.length),
    });
    area.appendChild(head);
    // The "held since" clock ticks in place with the rows below it.
    const meta = head.querySelector<HTMLElement>(".larea__meta");
    if (meta) liveUpdaters.push(() => {
      meta.textContent =
        `${linger.length} server${linger.length === 1 ? "" : "s"} · ` +
        `held since ${ago(Math.max(...linger.map((s) => s.closedMs ?? 0)))}`;
    });
    linger.forEach((s) => area.appendChild(renderServer(s)));
    body.appendChild(area);
  }

  if (live.length) {
    const area = el("div", "larea larea--live");
    area.appendChild(areaHead("Serving for a pane", live.length, {}));
    live.forEach((s) => area.appendChild(renderServer(s)));
    body.appendChild(area);
  }

  if (other.length) {
    const area = el("div", "larea larea--other");
    area.appendChild(areaHead("Other local servers", other.length, {}));
    other.forEach((s) => area.appendChild(renderServer(s)));
    body.appendChild(area);
  }

  return body;
}

/** The header sub-line count. Reads the filtered set so it agrees with the list
 *  and the sidebar card. */
function updateSub(): void {
  const sub = overlay?.querySelector(".lpanel__sub");
  if (!sub) return;
  const vis = visibleServers();
  const linger = vis.filter((s) => s.kind === "lingering").length;
  sub.textContent = vis.length
    ? `${vis.length} listening${linger ? ` · ${linger} lingering` : ""}`
    : "nothing listening";
}

/** Structural rebuild — only on new data, a kill, or a filter flip. Replaces the
 *  list wholesale, which is fine because it happens on an EVENT, not a timer. */
function rerender(): void {
  if (!overlay) return;
  const card = overlay.querySelector(".lpanel");
  const old = overlay.querySelector(".lpanel__body");
  if (!card || !old) return;
  card.replaceChild(renderBody(), old);
  updateSub();
}

/** The 1s heartbeat: refresh only the wall-clock-derived text in place. Keeping
 *  the existing DOM (rather than replaceChild) is what stops the hovered row's
 *  highlight from blinking once a second. */
function retick(): void {
  if (!overlay) return;
  updateSub();
  for (const fn of liveUpdaters) fn();
}

// ---------------------------------------------------------------- actions

async function confirmKill(s: LocalResourceView): Promise<void> {
  const who = s.kind === "lingering" && s.paneName ? ` (was “${s.paneName}”)` : "";
  const ok = await confirmDialog({
    title: "Kill this server?",
    body:
      `Stops ${s.framework} on port ${s.port} — pid ${s.pid}${who} — and its child ` +
      `processes. Anything unsaved in it is lost.`,
    confirmLabel: "Kill",
    danger: true,
  });
  if (!ok) return;
  deleting.add(s.pid);
  rerender();
  send({ type: "local.kill", pid: s.pid });
}

async function confirmKillLingering(count: number): Promise<void> {
  const ok = await confirmDialog({
    title: `Kill ${count} lingering server${count === 1 ? "" : "s"}?`,
    body:
      `${count === 1 ? "It" : "They"} outlived the pane${count === 1 ? "" : "s"} that ` +
      `started ${count === 1 ? "it" : "them"} and ${count === 1 ? "is" : "are"} still ` +
      `holding a port. This cannot be undone.`,
    confirmLabel: "Kill all",
    danger: true,
  });
  if (!ok) return;
  latest.filter((s) => s.kind === "lingering").forEach((s) => deleting.add(s.pid));
  rerender();
  send({ type: "local.killLingering" });
}

// ---------------------------------------------------------------- perch-only filter

/** The header's framed-checkbox filter. Accent frame always; its center fills
 *  only when "Perch only" is live — a hollow box that solidifies, not an on/off
 *  pill, so it reads as "narrow to mine" rather than a mode switch. */
function makeFilterToggle(): HTMLElement {
  const btn = el("button", "lpanel__filter") as HTMLButtonElement;
  btn.type = "button";
  btn.setAttribute("role", "switch");
  btn.setAttribute("aria-checked", String(perchOnly));
  btn.title = "Count only servers Perch started (hide ones started elsewhere)";
  const box = el("span", "lpanel__filter-box");
  box.setAttribute("aria-hidden", "true");
  btn.appendChild(box);
  btn.appendChild(el("span", "lpanel__filter-label", "Perch only"));
  btn.addEventListener("click", () => togglePerchOnly());
  return btn;
}

function syncFilterToggle(): void {
  overlay?.querySelector(".lpanel__filter")?.setAttribute("aria-checked", String(perchOnly));
}

/** User flipped the filter: repaint the panel + sidebar, and persist so it
 *  survives the panel closing and the app restarting. */
function togglePerchOnly(): void {
  perchOnly = !perchOnly;
  syncFilterToggle();
  rerender();
  updateSidebar();
  send({ type: "prefs.set", localPerchOnly: perchOnly });
}

/** Reflect the host's persisted pref (arrives on every state push). No persist
 *  and no work when it already matches — otherwise every push would repaint. */
function applyPerchOnlyPref(next: boolean): void {
  if (next === perchOnly) return;
  perchOnly = next;
  syncFilterToggle();
  rerender();
  updateSidebar();
}

// ---------------------------------------------------------------- lifecycle

export function applyLocalData(msg: LocalDataMessage): void {
  latest = msg.servers ?? [];
  for (const pid of [...deleting]) if (!latest.some((s) => s.pid === pid)) deleting.delete(pid);
  updateSidebar();
  rerender();
}

export function closeLocalPanel(): void {
  if (!overlay) return;
  overlay.remove();
  overlay = null;
  if (tick != null) { window.clearInterval(tick); tick = null; }
  send({ type: "local.panel", open: false });
}

export function showLocalPanel(): void {
  if (overlay) { closeLocalPanel(); return; }

  overlay = el("div", "local-overlay");
  overlay.addEventListener("click", (e) => { if (e.target === overlay) closeLocalPanel(); });

  const card = el("div", "lpanel");
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  card.setAttribute("aria-label", "Local dev servers");

  const head = el("div", "lpanel__head");
  head.appendChild(el("span", "lpanel__title", "Local"));
  head.appendChild(el("span", "lpanel__sub", "…"));
  head.appendChild(el("span", "lpanel__spacer"));
  head.appendChild(makeFilterToggle());

  const refresh = el("button", "lpanel__icon") as HTMLButtonElement;
  refresh.setAttribute("aria-label", "Rescan");
  refresh.innerHTML =
    '<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" ' +
    'stroke-width="1.3" stroke-linecap="round"><path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/>' +
    '<path d="M13.6 2.2v2.8h-2.8"/></svg>';
  refresh.addEventListener("click", () => send({ type: "local.refresh" }));
  head.appendChild(refresh);
  card.appendChild(head);

  card.appendChild(renderBody());

  const foot = el("div", "lpanel__foot");
  const note = el("span", undefined, "Loopback listeners · kill targets the exact pid");
  note.title =
    "Servers listening on 127.0.0.1 / ::1 / 0.0.0.0. Perch attributes each to the " +
    "pane that spawned it by walking the process tree; a server whose pane is gone " +
    "is “lingering”. Kill sends to the exact process id, never by name.";
  foot.appendChild(note);
  card.appendChild(foot);

  overlay.appendChild(card);
  document.body.appendChild(overlay);

  const esc = (e: KeyboardEvent) => {
    if (e.key !== "Escape" || !overlay) return;
    closeLocalPanel();
    window.removeEventListener("keydown", esc);
  };
  window.addEventListener("keydown", esc);

  // Uptime + "closed Xm ago" are wall-clock derived, so refresh once a second
  // while open. retick (not rerender) so the hovered row survives the tick.
  tick = window.setInterval(retick, 1000);

  send({ type: "local.panel", open: true });
  rerender();
}

// ---------------------------------------------------------------- sidebar area

/** The sidebar LOCAL area. Like cloud, it doesn't exist unless something is
 * listening — so its mere presence says "you have dev servers up". Amber at
 * rest; escalates to caution and grows an alert row when one has outlived its
 * pane, so what needs you changes the card's SIZE, not just its color. */
function updateSidebar(): void {
  const area = document.getElementById("local-area");
  const card = document.getElementById("local-card");
  if (!area || !card) return;

  // Presence tracks the RAW set, not the filter: even when "Perch only" hides
  // everything, the card must stay so the panel — and the toggle inside it —
  // remain reachable.
  if (latest.length === 0) {
    area.hidden = true;
    return;
  }
  area.hidden = false;

  const vis = visibleServers();
  const linger = vis.filter((s) => s.kind === "lingering");
  const live = vis.filter((s) => s.kind === "live");
  // Filter on, but nothing of ours is up (only "other" servers listening).
  const noneOfOurs = perchOnly && vis.length === 0;

  const title = document.getElementById("local-card-title");
  const portEl = document.getElementById("local-card-port");
  const sub = document.getElementById("local-card-sub");
  if (title) title.textContent = noneOfOurs
    ? "No Perch servers"
    : `${vis.length} server${vis.length === 1 ? "" : "s"}`;

  // Trailing port: the newest live server's, else the newest of anything shown.
  if (portEl) {
    const pick = (live.length ? live : vis)
      .slice()
      .sort((a, b) => b.startedMs - a.startedMs)[0];
    portEl.textContent = pick ? `:${pick.port}` : "";
  }

  if (sub) {
    if (noneOfOurs) {
      sub.textContent = `${latest.length} started outside Perch`;
    } else {
      // Distinct frameworks of the shown set.
      const fw = [...new Set(vis.map((s) => s.framework))].slice(0, 3);
      sub.textContent = fw.length ? fw.join(" · ") : "listening";
    }
  }

  card.classList.toggle("local-card--linger", linger.length > 0);
  // Outline↔fill: the card fills with amber only when a Perch server is actually
  // live (a pane owns it). Lingering escalates to caution and takes precedence,
  // so the fill and the caution wash never fight over the same card.
  card.classList.toggle("local-card--live", linger.length === 0 && live.length > 0);

  const alert = document.getElementById("local-card-alert");
  const alertText = document.getElementById("local-card-alert-text");
  const alertMeta = document.getElementById("local-card-alert-meta");
  if (alert) alert.hidden = linger.length === 0;
  if (linger.length && alertText && alertMeta) {
    alertText.textContent = `${linger.length} lingering`;
    alertMeta.textContent = ago(Math.max(...linger.map((s) => s.closedMs ?? 0)));
  }

  card.title = linger.length
    ? `${linger.length} server${linger.length === 1 ? "" : "s"} still listening with no pane`
    : noneOfOurs
      ? `${latest.length} local server${latest.length === 1 ? "" : "s"} listening, none started by Perch`
      : `${vis.length} local server${vis.length === 1 ? "" : "s"} listening`;
}

export function initLocal(): void {
  document.getElementById("local-card")?.addEventListener("click", () => showLocalPanel());
  onMessage((msg) => {
    if (msg.type === "local.data") applyLocalData(msg);
    // The "Perch only" filter rides in prefs on every state push, like the
    // Inspector's open state — mirror it so a toggle in one window (or a restart)
    // is reflected here.
    else if (msg.type === "state") applyPerchOnlyPref(msg.prefs?.localPerchOnly ?? false);
  });
  // Uptime + "closed Xm ago" on the sidebar would otherwise sit frozen between
  // scans. Cheap, and only while servers actually exist.
  window.setInterval(() => {
    if (latest.length) updateSidebar();
  }, 10_000);
}
