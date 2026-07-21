// Sidebar = the session list + the New Session button. Hand-rolled because
// the chrome is small and we want the DOM to mirror the host state shape
// exactly. Reconciles diff-free: clear + rebuild on every state push.
//
// The list is split into sections driven purely by derived agent state:
//   - "Needs you" : sessions blocked on a permission prompt (or a genuine,
//                   reserved "waiting"). The agent CAN'T proceed without you.
//                   Each row also shows the agent's note (its ask).
//   - "Idle"      : sessions whose agent finished its turn ("done") and is at
//                   rest — your move, but nothing is blocked and there's no
//                   rush. Calm, single-line rows, no alarming note.
//   - "Projects"  : everything else (working / dormant idle), single-line rows.
// Each row is a framed card (see .session-item in style.css) — perch-style
// tabs-as-cards. Selection still reads via fill, not an accent stripe.

import type {
  SessionView,
  ClosedSessionView,
  PaneTreeView,
  ProjectView,
  SidebarMode,
} from "./bridge.js";
import { send } from "./bridge.js";
import { confirmDialog, confirmWithOption } from "./confirm.js";
import { showNewTabDialog } from "./new-tab-dialog.js";
import { elapsedSpan, agoSpan, ageSpan } from "./elapsed.js";
import { spinnerSpan } from "./spinner.js";
import { attachCommitsHover, openCommitsLightbox } from "./commits-view.js";

/** Flatten a pane tree to its leaves. */
function leaves(node: PaneTreeView): Array<Extract<PaneTreeView, { kind: "leaf" }>> {
  return node.kind === "leaf" ? [node] : node.children.flatMap(leaves);
}

/** The pane whose unpushed-commit recap the session's ↑N represents. */
function aheadPaneId(s: SessionView): string | null {
  const ls = leaves(s.rootPane);
  return (ls.find((l) => l.ahead === s.ahead && s.ahead > 0) ?? ls[0])?.paneId ?? null;
}

/** A project group's unpushed-commit count, deduped by branch.
 *
 *  `ahead` is `@{upstream}..HEAD` — a property of the BRANCH, not the tab. N
 *  tabs open on one branch each honestly report the same count, so summing
 *  per-tab multiplies one branch's work by its tab count: five tabs on
 *  product-tools-prod's main, all correctly reading ↑6, rendered a ↑30 header.
 *  Git won't check out one branch in two worktrees, so within a project the
 *  branch names a worktree uniquely — count each branch once.
 *
 *  A tab whose branch is unknown ("") can't be PROVEN a duplicate, so it keeps
 *  its own key instead of collapsing into the other unknowns. Over-counting an
 *  unresolved tab beats silently swallowing a real branch's commits.
 *
 *  `top` is the biggest single contributor — the pane whose recap the chip
 *  opens, since the recap is per-pane and can't show a union.
 */
export function projectAhead(tabs: SessionView[]): { sum: number; top: SessionView | null } {
  const byBranch = new Map<string, SessionView>();
  for (const t of tabs) {
    if (t.ahead <= 0) continue;
    // Git forbids spaces (and control chars) in ref names, so a
    // space-prefixed id is a sentinel no real branch can collide with.
    const key = t.branch || ` ${t.id}`;
    const prev = byBranch.get(key);
    if (!prev || t.ahead > prev.ahead) byBranch.set(key, t);
  }
  let sum = 0;
  let top: SessionView | null = null;
  for (const t of byBranch.values()) {
    sum += t.ahead;
    if (!top || t.ahead > top.ahead) top = t;
  }
  return { sum, top };
}

/** Accent "↑N ready to push" chip — hover shows the commit recap, click opens
 *  the full lightbox. One builder for BOTH surfaces that wear it (a tab row's
 *  meta line and the project group header) so the behavior can't drift.
 *  `paneId` is the pane whose recap the count represents; without one the chip
 *  is a plain, non-interactive count. */
function aheadChip(ahead: number, paneId: string | null, extraClass = ""): HTMLElement {
  const span = document.createElement("span");
  span.className =
    "session-item__meta-item session-item__meta-item--ahead" +
    (extraClass ? ` ${extraClass}` : "");
  span.textContent = `↑${ahead}`;
  if (paneId) {
    // Rich hover (the commit recap card) + click (the lightbox). No native
    // `title` here: it would pop the OS tooltip "Commits ready to push" ON TOP
    // of the custom hover card — two overlapping popups for one chip.
    attachCommitsHover(span, paneId);
    span.addEventListener("click", (ev) => {
      // Both hosts are buttons (the row selects the session, the header
      // toggles the fold) — keep the chip's click from also doing that.
      ev.stopPropagation();
      openCommitsLightbox(paneId);
    });
  } else {
    // A plain, non-interactive count (no pane to recap) — the native tooltip is
    // then the only affordance, and there's no custom hover for it to fight.
    span.title = "Commits ready to push";
  }
  return span;
}

export type ProjectGroup = { project: ProjectView; tabs: SessionView[] };

/** Most-urgent state across a project's tabs — the same priority the host uses
 *  per session (permission > waiting > done > working > idle). Drives the dot on
 *  a COLLAPSED project header: folding a group away must never be able to hide
 *  an agent that's blocked on you. */
const STATE_RANK: Record<string, number> = {
  permission: 4,
  waiting: 3,
  done: 2,
  working: 1,
  idle: 0,
};
export function aggregateState(tabs: SessionView[]): SessionView["agentState"] {
  let best: SessionView["agentState"] = "idle";
  for (const t of tabs)
    if ((STATE_RANK[t.agentState] ?? 0) > (STATE_RANK[best] ?? 0)) best = t.agentState;
  return best;
}

/**
 * Files sessions under their projects for project mode.
 *
 * `other` is the safety net and the reason this is a function worth testing: a
 * session belongs there when it has no project, but ALSO when it points at a
 * project that no longer exists (you unregistered the repo while its tab was
 * open). Without that second case such a tab would match no group and no
 * "Other" — it would simply vanish from the sidebar while still running.
 *
 * Projects keep their registration order (stable rows); tabs keep host order.
 * A project with no tabs still gets a group, so its "+" stays reachable.
 */
export function groupByProject(
  sessions: SessionView[],
  projects: ProjectView[]
): { groups: ProjectGroup[]; other: SessionView[] } {
  const known = new Set(projects.map((p) => p.id));
  const byId = new Map<string, SessionView[]>();
  const other: SessionView[] = [];

  for (const s of sessions) {
    if (!s.projectId || !known.has(s.projectId)) {
      other.push(s);
      continue;
    }
    const list = byId.get(s.projectId);
    if (list) list.push(s);
    else byId.set(s.projectId, [s]);
  }

  return {
    groups: projects.map((project) => ({ project, tabs: byId.get(project.id) ?? [] })),
    other,
  };
}

const COLLAPSED_KEY = "perch.projects.collapsed";

export class Sidebar {
  private readonly listEl: HTMLElement;
  private readonly newSessionBtn: HTMLElement;
  private readonly closedEl: HTMLElement;

  /** Collapsed project headers. Page-local (localStorage) rather than host
   *  state: it's a per-view preference, it changes on every click, and pushing
   *  it through the host would mean a round-trip and a state push just to fold
   *  a row. */
  private readonly collapsed = new Set<string>();

  /** Set by main.ts to re-run the last render — a collapse toggle changes only
   *  page-local state, so there's no host push to ride back in on. */
  rerender?: () => void;

  /** The project whose header was JUST toggled, if any. The sidebar rebuilds its
   *  DOM from scratch on every state push, so a CSS `transition` can't animate a
   *  fold (the node is born at its final angle), and a plain entry animation
   *  would re-fire on every push — the list would flicker once a second while an
   *  agent works. So the animation is opt-in, for one render, on the one project
   *  you actually clicked. */
  private justToggled: string | null = null;

  /** In-flight sidebar drag (a project header or a tab row), or null. */
  private drag: { kind: "project" | "tab"; id: string; el: HTMLElement } | null = null;

  /** Last mode rendered — a Sessions ⇄ Projects flip plays a soft cross-fade of
   *  the list (null until the first render, so launch doesn't animate). */
  private lastMode: SidebarMode | null = null;

  constructor(listEl: HTMLElement, newSessionBtn: HTMLElement, closedEl: HTMLElement) {
    this.listEl = listEl;
    this.newSessionBtn = newSessionBtn;
    this.closedEl = closedEl;

    this.newSessionBtn.addEventListener("click", () => {
      send({ type: "session.new" });
    });

    // Drag-reorder for projects + tabs. Delegated on the (stable) list container
    // so it survives the full re-render on every state push; rows just set
    // `draggable` and their id/projectId data attributes.
    this.listEl.addEventListener("dragstart", (ev) => this.onDragStart(ev));
    this.listEl.addEventListener("dragover", (ev) => this.onDragOver(ev));
    this.listEl.addEventListener("drop", (ev) => this.onDrop(ev));
    this.listEl.addEventListener("dragend", () => this.onDragEnd());

    try {
      const raw = localStorage.getItem(COLLAPSED_KEY);
      if (raw) for (const id of JSON.parse(raw) as string[]) this.collapsed.add(id);
    } catch {
      /* corrupt/unavailable storage → start expanded. Not worth failing over. */
    }
  }

  private persistCollapsed() {
    try {
      localStorage.setItem(COLLAPSED_KEY, JSON.stringify([...this.collapsed]));
    } catch {
      /* best-effort */
    }
  }

  // ---- Drag-reorder ---------------------------------------------------------

  private onDragStart(ev: DragEvent) {
    const t = ev.target as HTMLElement;
    const tab = t.closest<HTMLElement>(".session-item--nested");
    const proj = t.closest<HTMLElement>(".project-header");
    const el = tab ?? proj;
    if (!el) return;
    const kind: "project" | "tab" = tab ? "tab" : "project";
    const id = tab ? tab.dataset.sessionId : proj!.dataset.projectId;
    if (!id) return;
    this.drag = { kind, id, el };
    el.classList.add("sidebar-dragging");
    if (ev.dataTransfer) {
      ev.dataTransfer.effectAllowed = "move";
      ev.dataTransfer.setData("text/plain", id);
    }
  }

  private onDragOver(ev: DragEvent) {
    const target = this.dropTarget(ev.target as HTMLElement);
    if (!target) { this.clearDropMarks(); return; }
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = "move";
    this.mark(target, this.edgeOf(target, ev));
  }

  private onDrop(ev: DragEvent) {
    const d = this.drag;
    const target = this.dropTarget(ev.target as HTMLElement);
    this.clearDropMarks();
    if (!d || !target) return;
    ev.preventDefault();
    const targetId = d.kind === "project" ? target.dataset.projectId : target.dataset.sessionId;
    if (!targetId) return;
    send({ type: "sidebar.reorder", kind: d.kind, movedId: d.id, targetId, edge: this.edgeOf(target, ev) });
  }

  private onDragEnd() {
    this.drag?.el.classList.remove("sidebar-dragging");
    this.drag = null;
    this.clearDropMarks();
  }

  /** The row under the pointer that's a valid drop for the current drag: same
   *  kind, not itself, and (for tabs) a sibling in the SAME project — a reorder
   *  must never re-file a tab into another project. */
  private dropTarget(from: HTMLElement): HTMLElement | null {
    const d = this.drag;
    if (!d) return null;
    const target = from.closest<HTMLElement>(
      d.kind === "project" ? ".project-header" : ".session-item--nested");
    if (!target || target === d.el) return null;
    if (d.kind === "tab") {
      const a = d.el.dataset.projectId ?? "";
      if (a === "" || a !== (target.dataset.projectId ?? "")) return null;
    }
    return target;
  }

  private edgeOf(el: HTMLElement, ev: DragEvent): "before" | "after" {
    const r = el.getBoundingClientRect();
    return ev.clientY - r.top < r.height / 2 ? "before" : "after";
  }

  private mark(el: HTMLElement, edge: "before" | "after") {
    this.clearDropMarks();
    el.classList.add(edge === "before" ? "drop-before" : "drop-after");
  }

  private clearDropMarks() {
    for (const e of this.listEl.querySelectorAll(".drop-before, .drop-after"))
      e.classList.remove("drop-before", "drop-after");
  }

  render(
    sessions: SessionView[],
    activeId: string,
    closed: ClosedSessionView[] = [],
    projects: ProjectView[] = [],
    mode: SidebarMode = "sessions"
  ) {
    this.renderClosed(closed);
    const modeChanged = this.lastMode !== null && mode !== this.lastMode;
    this.lastMode = mode;
    if (mode === "projects") {
      this.listEl.replaceChildren(this.renderProjects(sessions, activeId, projects));
      this.playModeSwap(modeChanged);
      return;
    }
    // Partition by derived state. permission (+ the reserved "waiting") want
    // your attention (Needs you); done is "finished, at rest, your move" (Idle);
    // working/dormant-idle are just the map (Projects). Order within each
    // section follows the host's session order (stable) — no resort, so rows
    // don't jump.
    const needs = sessions.filter(
      (s) => s.agentState === "waiting" || s.agentState === "permission"
    );
    const idle = sessions.filter((s) => s.agentState === "done");
    const rest = sessions.filter(
      (s) => s.agentState === "working" || s.agentState === "idle"
    );

    const frag = document.createDocumentFragment();

    if (needs.length) {
      frag.appendChild(this.sectionLabel("Needs you", needs.length));
      const list = document.createElement("div");
      list.className = "session-list";
      for (const s of needs)
        list.appendChild(this.renderItem(s, s.id === activeId, true));
      frag.appendChild(list);
    }

    if (idle.length) {
      frag.appendChild(this.sectionLabel("Idle", idle.length));
      const list = document.createElement("div");
      list.className = "session-list";
      for (const s of idle)
        list.appendChild(this.renderItem(s, s.id === activeId, false));
      frag.appendChild(list);
    }

    if (rest.length) {
      frag.appendChild(this.sectionLabel("Projects"));
      const list = document.createElement("div");
      list.className = "session-list";
      for (const s of rest)
        list.appendChild(this.renderItem(s, s.id === activeId, false));
      frag.appendChild(list);
    }

    this.listEl.replaceChildren(frag);
    this.playModeSwap(modeChanged);
  }

  /** Soft cross-fade when the Sessions ⇄ Projects mode flips. Restarts the
   *  keyframe by removing + reflowing before re-adding. */
  private playModeSwap(changed: boolean) {
    if (!changed) return;
    const el = this.listEl;
    el.classList.remove("sidebar__scroll--swap");
    void el.offsetWidth;
    el.classList.add("sidebar__scroll--swap");
  }

  // "Recently closed" list, pinned above the identity footer. Each row
  // restores the whole session (layout + cwd + Claude resume) on click,
  // behind a confirm; a hover-revealed ✕ discards it from the list. Hidden
  // entirely when nothing's been closed.
  private renderClosed(closed: ClosedSessionView[]) {
    if (!closed.length) {
      this.closedEl.hidden = true;
      this.closedEl.replaceChildren();
      return;
    }
    this.closedEl.hidden = false;

    const frag = document.createDocumentFragment();

    const header = document.createElement("div");
    header.className = "recently-closed__header";
    const label = document.createElement("span");
    label.className = "recently-closed__label";
    label.textContent = "Recently closed";
    const count = document.createElement("span");
    count.className = "recently-closed__count";
    count.textContent = String(closed.length);
    header.append(label, count);
    frag.appendChild(header);

    const list = document.createElement("div");
    list.className = "recently-closed__list";
    for (const c of closed) list.appendChild(this.renderClosedRow(c));
    frag.appendChild(list);

    this.closedEl.replaceChildren(frag);
  }

  private renderClosedRow(c: ClosedSessionView): HTMLElement {
    const panes = c.paneCount === 1 ? "1 pane" : `${c.paneCount} panes`;

    const row = document.createElement("button");
    row.type = "button";
    row.className = "closed-item";
    row.dataset.sessionId = c.id;
    row.title = `Restore ${c.title}`;

    const icon = restoreIcon();
    icon.classList.add("closed-item__icon");
    row.appendChild(icon);

    const text = document.createElement("span");
    text.className = "closed-item__text";

    const title = document.createElement("span");
    title.className = "closed-item__title";
    title.textContent = c.title;
    text.appendChild(title);

    const meta = document.createElement("span");
    meta.className = "closed-item__meta";
    meta.append(panes);
    if (c.resumableCount > 0) {
      const agents = c.resumableCount === 1 ? "1 agent" : `${c.resumableCount} agents`;
      meta.append(` · ${agents}`);
    }
    if (c.closedAtMs > 0) {
      meta.append(" · ");
      meta.appendChild(agoSpan(c.closedAtMs));
    }
    text.appendChild(meta);
    row.appendChild(text);

    // Discard from the list (no restore). Stops the row's restore handler.
    const purge = document.createElement("button");
    purge.type = "button";
    purge.className = "closed-item__purge";
    purge.title = "Remove from recently closed";
    purge.setAttribute("aria-label", `Remove ${c.title} from recently closed`);
    purge.textContent = "✕";
    purge.addEventListener("click", (ev) => {
      ev.stopPropagation();
      send({ type: "session.purge", id: c.id });
    });
    row.appendChild(purge);

    // Restoring relaunches agents, so gate it behind a confirm (the user
    // asked for "are you sure?" before bringing a closed project back).
    row.addEventListener("click", async () => {
      const agents =
        c.resumableCount > 0
          ? ` and resume ${
              c.resumableCount === 1 ? "its Claude session" : `${c.resumableCount} Claude sessions`
            }`
          : "";
      const ok = await confirmDialog({
        title: `Restore ${c.title}?`,
        body: `Reopen this session's ${panes}${agents}.`,
        confirmLabel: "Restore",
        cancelLabel: "Cancel",
      });
      if (ok) send({ type: "session.restore", id: c.id });
    });

    return row;
  }

  // Project mode: one collapsible header per registered repo, its tabs nested
  // beneath. Rows are the SAME renderItem as session mode, so the loc / commit /
  // ahead chips come along unchanged — the whole point of filing tabs under a
  // project is being able to read them per tab.
  //
  // Sessions that aren't filed under a project land in a trailing "Other" group,
  // so nothing can silently disappear from the sidebar just because it predates
  // project mode or lives outside a registered repo.
  private renderProjects(
    sessions: SessionView[],
    activeId: string,
    projects: ProjectView[]
  ): DocumentFragment {
    const frag = document.createDocumentFragment();

    // No projects yet → the empty state IS the registration prompt. It clears
    // itself the moment a project exists, so there's no "ask once, remember the
    // answer forever" flag to get stuck in the wrong position.
    if (projects.length === 0) {
      frag.appendChild(this.renderProjectsEmpty());
      return frag;
    }

    // NOTE: unfiled sessions are deliberately NOT shown here. They used to land
    // in an "Other" bucket, which made no sense in a view that is *about*
    // projects — a scratch shell in your home directory isn't a project, and
    // listing it under the projects made the view read as a second, worse copy
    // of the session list. The two modes now answer different questions, and
    // Sessions mode (one click away) is where an unfiled session lives.
    const { groups } = groupByProject(sessions, projects);

    for (const { project, tabs } of groups) {
      const collapsed = this.collapsed.has(project.id);
      const animate = this.justToggled === project.id;
      frag.appendChild(
        this.projectHeader(project, tabs, collapsed, aggregateState(tabs), animate)
      );
      // Collapsed means collapsed — every tab folds away, including the active
      // one. Keeping the active tab visible under a closed chevron made the
      // control contradict itself: the arrow said "shut" while a row sat right
      // below it. You don't lose your place — the pane header and status bar
      // still name the session — and an agent that needs you can't hide, because
      // the header wears its group's state as a dot.
      if (!collapsed && tabs.length) {
        const list = this.sessionList(tabs, activeId, true);
        // Only the just-unfolded group animates in. (Folding shut is immediate:
        // animating a removal means keeping the node alive past its state, and a
        // fold that lingers reads as lag, not polish.)
        if (animate) list.classList.add("session-list--enter");
        frag.appendChild(list);
      }
    }
    this.justToggled = null;   // one render only

    // A PERMANENT way to add another project. This used to live only in the
    // empty state — which vanishes the moment you register your first repo, so
    // there was then no door at all: you could set your scan folders in Settings
    // and have nothing anywhere to act on them.
    frag.appendChild(this.renderAddProject());
    return frag;
  }

  // `nested` = a project's tab list. Denser than the flat session list: the
  // project header already does the grouping, so the per-row card frame is
  // redundant weight. Dropping it (and tightening the gaps) is what lets several
  // projects × several tabs fit on screen at once — the whole point of the view.
  private sessionList(
    sessions: SessionView[],
    activeId: string,
    nested = false
  ): HTMLElement {
    const list = document.createElement("div");
    list.className = "session-list" + (nested ? " session-list--nested" : "");
    for (const s of sessions) {
      const needsNote =
        s.agentState === "waiting" || s.agentState === "permission";
      const item = this.renderItem(s, s.id === activeId, needsNote, nested);
      if (nested) {
        item.classList.add("session-item--nested");
        item.draggable = true;                          // drag-reorder within its project
        item.dataset.projectId = s.projectId ?? "";     // constrains drops to same-project siblings
      }
      list.appendChild(item);
    }
    return list;
  }

  private projectHeader(
    p: ProjectView,
    tabs: SessionView[],
    collapsed: boolean,
    state: SessionView["agentState"],
    animate = false
  ): HTMLElement {
    const tabCount = tabs.length;
    const row = document.createElement("div");
    row.className = "project-header";
    row.dataset.projectId = p.id;
    row.draggable = true;   // drag-reorder handle for the whole group

    // The header body is the collapse toggle. A button (not the whole row) so
    // the "+" stays independently clickable and keyboard-reachable.
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "project-header__toggle";
    toggle.title = p.path;

    // A project with no tabs has nothing to expand, so it isn't a toggle at all:
    // no chevron, and clicking does nothing. Offering to unfold an empty group
    // (and having it visibly do nothing) is just a broken-feeling control.
    const empty = tabCount === 0;
    toggle.disabled = empty;
    if (!empty) toggle.setAttribute("aria-expanded", String(!collapsed));

    // An SVG chevron, not a "›" glyph. The glyph's ink sits off-centre in its em
    // box, so rotating it swung the mark around a point that isn't its middle —
    // it visibly lurched sideways instead of pivoting. An SVG rotates about the
    // centre of its own viewBox, which is what the eye expects.
    const chev = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    chev.setAttribute("class", "project-header__chev");
    chev.setAttribute("viewBox", "0 0 12 12");
    chev.setAttribute("width", "12");
    chev.setAttribute("height", "12");
    chev.setAttribute("fill", "none");
    chev.setAttribute("aria-hidden", "true");
    chev.dataset.collapsed = String(collapsed);
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", "M4.5 2.5 L8 6 L4.5 9.5");   // points right; CSS turns it down
    path.setAttribute("stroke", "currentColor");
    path.setAttribute("stroke-width", "1.4");
    path.setAttribute("stroke-linecap", "round");
    path.setAttribute("stroke-linejoin", "round");
    chev.appendChild(path);
    if (animate) chev.classList.add("project-header__chev--turning");
    // Keep the slot even when empty, so names stay aligned down the column.
    if (empty) chev.style.visibility = "hidden";
    toggle.appendChild(chev);

    const name = document.createElement("span");
    name.className = "project-header__name";
    name.textContent = p.name;
    toggle.appendChild(name);

    if (tabCount) {
      const count = document.createElement("span");
      count.className = "project-header__count";
      count.textContent = String(tabCount);
      toggle.appendChild(count);
    }

    // Collapsed only: the rows (and their dots) are hidden, so the header
    // carries their most-urgent state. A permission-blocked agent must never be
    // foldable out of sight. Expanded, the rows speak for themselves.
    if (collapsed && state !== "idle") {
      const dot = document.createElement("span");
      dot.className = "project-header__state";
      dot.dataset.state = state;
      dot.title = `${p.name}: ${state}`;
      toggle.appendChild(dot);
    }

    toggle.addEventListener("click", () => {
      if (empty) return;   // nothing to unfold
      if (this.collapsed.has(p.id)) this.collapsed.delete(p.id);
      else this.collapsed.add(p.id);
      this.persistCollapsed();
      this.justToggled = p.id;   // animate THIS one, this render only
      this.rerender?.();
    });
    row.appendChild(toggle);

    // Unpushed commits across the project's tabs, at the trailing edge — one
    // count per branch, NOT per tab (see projectAhead). The per-tab ↑N hides
    // when a tab is inactive (metrics fold behind the hover-ⓘ) or the group is
    // collapsed — this keeps "there's work ready to push in here" readable from
    // the header line alone. The recap it opens is driven by the pane of the
    // tab contributing the most unpushed commits (the recap is per-pane; the
    // biggest contributor is the most useful single answer to "what's in
    // there?"). Hidden when the sum is 0.
    const { sum: aheadSum, top } = projectAhead(tabs);
    if (aheadSum > 0 && top) {
      row.appendChild(aheadChip(aheadSum, aheadPaneId(top), "project-header__ahead"));
    }

    const add = document.createElement("button");
    add.type = "button";
    add.className = "project-header__add";
    add.title = `New tab in ${p.name}`;
    add.setAttribute("aria-label", `New tab in ${p.name}`);
    add.textContent = "+";
    add.addEventListener("click", (e) => {
      e.stopPropagation();
      showNewTabDialog(p);
    });
    row.appendChild(add);

    return row;
  }

  /** "Add project" — always present in project mode, under the last group. */
  private renderAddProject(): HTMLElement {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "project-add";
    btn.title = "Find repos to register as projects";

    const plus = document.createElement("span");
    plus.className = "project-add__plus";
    plus.textContent = "+";
    plus.setAttribute("aria-hidden", "true");
    btn.appendChild(plus);

    const label = document.createElement("span");
    label.textContent = "Add project";
    btn.appendChild(label);

    // Same scan the empty state runs: offers the repos you already have open,
    // plus a one-level scan of your configured folders, plus a folder picker.
    btn.addEventListener("click", () => send({ type: "projects.scan" }));
    return btn;
  }

  /**
   * The zero state. This is the only thing in the sidebar when you first switch
   * to project mode, so it has to do the explaining: what a project IS, why
   * you'd want one, and the two ways to make one. Centered — an empty state is
   * one of the two places the constitution allows it.
   */
  private renderProjectsEmpty(): HTMLElement {
    const box = document.createElement("div");
    box.className = "projects-empty";

    // The app icon (the monocled bird), same mark as the window and taskbar.
    const mark = document.createElement("img");
    mark.className = "projects-empty__mark";
    mark.src = "/perch-logo.png";
    mark.alt = "";
    mark.setAttribute("aria-hidden", "true");
    box.appendChild(mark);

    const title = document.createElement("div");
    title.className = "projects-empty__title";
    title.textContent = "Work by project";
    box.appendChild(title);

    const body = document.createElement("div");
    body.className = "projects-empty__body";
    body.textContent =
      "Register a repo to keep its tabs together. Each tab can run in its own git " +
      "worktree, so two agents never overwrite each other's files — and every tab " +
      "counts only its own changes.";
    box.appendChild(body);

    const actions = document.createElement("div");
    actions.className = "projects-empty__actions";

    const scan = document.createElement("button");
    scan.type = "button";
    scan.className = "projects-empty__btn projects-empty__btn--primary";
    scan.textContent = "Find repos";
    scan.addEventListener("click", () => send({ type: "projects.scan" }));
    actions.appendChild(scan);

    const browse = document.createElement("button");
    browse.type = "button";
    browse.className = "projects-empty__btn";
    browse.textContent = "Add a folder…";
    browse.addEventListener("click", () => send({ type: "project.browse" }));
    actions.appendChild(browse);

    box.appendChild(actions);

    // Where "Find repos" looks, so an empty scan isn't a dead end.
    const hint = document.createElement("div");
    hint.className = "projects-empty__hint";
    hint.textContent = "Find repos searches your open panes and your scan folders.";
    box.appendChild(hint);

    return box;
  }

  private sectionLabel(text: string, count?: number): HTMLElement {
    const el = document.createElement("div");
    el.className = "sidebar__section-label";
    el.textContent = text;
    if (count != null) {
      const c = document.createElement("span");
      c.className = "sidebar__section-count";
      c.textContent = ` · ${count}`;
      el.appendChild(c);
    }
    return el;
  }

  /** `compact` = a project's nested tab row: title + ONE ellipsized meta line
   *  (2 rows total), smaller type, and a timer glyph in place of the word
   *  "finished" — several projects × several tabs have to fit on screen. */
  private renderItem(
    s: SessionView,
    active: boolean,
    showNote: boolean,
    compact = false
  ): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "session-item" + (active ? " session-item--active" : "");
    item.dataset.sessionId = s.id;

    // Status column. At rest it's the state dot (CSS colors it via
    // [data-state]); a WORKING tab in compact mode animates instead — the same
    // braille spinner cc draws in the pane, so the sidebar echoes the
    // terminal. The spinner replaces the dot in its own 10px slot: same
    // footprint, and "moving = running" needs no legend.
    //
    // "Working" here means ANY pane is working, not the aggregate state: the
    // host ranks Done above Working (so a finished turn isn't hidden by a
    // busy sibling), but a tab with a live pane must never read as at-rest.
    // turnStartMs is projected from the earliest working pane, so the elapsed
    // chip ticks correctly in this mixed case too.
    const working = compact && (s.agentState === "working" || s.workingCount > 0);
    let statusEl: HTMLElement;
    if (working) {
      statusEl = spinnerSpan("session-item__spinner");
    } else {
      statusEl = document.createElement("span");
      statusEl.className = "session-item__dot";
      statusEl.dataset.state = s.agentState;
    }
    // Status column is a fixed 10px slot (keeps every title left-aligned). A tab
    // that owns a live dev server gets a small amber "serving" pip in the corner
    // of its status dot — the sidebar's glanceable "this tab is up on a port"
    // signal. Amber (--color-local) is the reserved local-server hue; the exact
    // port lives on the pane header's chip. A corner pip (not a second dot)
    // keeps col1 at 10px so no title shifts. Wrapped so the pip can anchor to it.
    const lead = document.createElement("span");
    lead.className = "session-item__lead";
    lead.appendChild(statusEl);
    if (s.ports && s.ports.length > 0) {
      const pip = document.createElement("span");
      pip.className = "session-item__serving";
      pip.title = `Serving ${s.ports.map((p) => `:${p}`).join("  ")}`;
      lead.appendChild(pip);
    }
    item.appendChild(lead);

    // Primary line: title only. The shell rides in the footer; the row gives
    // the title the whole line for a single clean ellipsis.
    const primary = document.createElement("span");
    primary.className = "session-item__primary";
    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = s.title;
    primary.appendChild(title);
    item.appendChild(primary);

    // The tab's /color tag deliberately does NOT mark the sidebar row: we
    // tried a title tint, a trailing dot (ellipsis swallowed it on long
    // names), and a pre-title line — all noise next to an informative title.
    // The color lives in the pane header (and cc's own theme) only.

    // Secondary metrics (state signal, loc, branch, panes — see buildMeta).
    // In compact (projects) mode only the ACTIVE tab spends a line on them:
    // the other rows stay a single title line and fold the same metrics
    // behind a quiet hover-ⓘ, so a tall project list scans as titles, not
    // numbers. Session mode keeps the full meta on every row.
    //
    // A compact WORKING row is special-cased to ONE line with no prose at
    // all: the spinner says "running", a ticking elapsed rides the right
    // edge, and the "what exactly is it doing" line lives on the spinner's
    // hover tip (and in the pane header you're about to click anyway).
    if (working) {
      if (s.turnStartMs > 0) {
        if (active) {
          // The active row shows the close ✕ permanently in column 3, so its
          // elapsed takes the slim meta line instead of fighting the ✕.
          const meta = document.createElement("span");
          meta.className = "session-item__meta session-item__meta--compact";
          meta.appendChild(elapsedSpan(s.turnStartMs));
          item.appendChild(meta);
        } else {
          // Right-edge chip, same cell as the hover-revealed ✕ — CSS fades
          // the chip out as the ✕ fades in.
          const chip = document.createElement("span");
          chip.className = "session-item__chip";
          chip.appendChild(elapsedSpan(s.turnStartMs));
          item.appendChild(chip);
        }
      }
    } else if (compact && !active) {
      // Metrics are no longer behind a per-row ⓘ; they slide in on row hover
      // (appended below the branches). Here we keep only the always-on age.
      // A DONE row is a row waiting on YOU — the turn came back and nothing
      // has been typed since. Every green dot in a tall list is equally
      // actionable, which is exactly why the list stops being scannable past
      // a handful: nothing says which one you dropped a moment ago and which
      // has been parked since morning. So the age comes out from behind the
      // ⓘ hover and rides the right edge, in the same grid cell (and the same
      // fade-on-hover) as the close ✕. CSS decays it via data-warmth; the dot
      // stays uniform so warmth never competes with the state palette.
      if (s.agentState === "done" && s.doneAtMs > 0) {
        const chip = document.createElement("span");
        chip.className = "session-item__chip session-item__age";
        chip.title = "Time since the agent handed the turn back";
        chip.appendChild(ageSpan(s.doneAtMs));
        item.appendChild(chip);
      }
    } else if (compact && active && s.agentState === "done" && s.doneAtMs > 0) {
      // Same age on the ACTIVE done row, which can't use the chip cell — the
      // close ✕ lives there permanently. It rides the END OF THE TITLE LINE
      // instead (margin-left:auto inside .session-item__primary), NOT the meta
      // line below: the ages form a right-edge column the eye scans down, and
      // a label that drops a line and jumps left breaks that column on the one
      // row you're most likely looking at.
      const chip = document.createElement("span");
      chip.className = "session-item__age session-item__age--inline";
      chip.title = "Time since the agent handed the turn back";
      chip.appendChild(ageSpan(s.doneAtMs));
      primary.appendChild(chip);
    } else {
      const meta = this.buildMeta(s, compact);
      if (meta) item.appendChild(meta);
    }

    // Compact INACTIVE rows stay one title line at rest; hovering the row slides
    // the metrics in as a second line INSIDE the row (replacing the old per-row
    // ⓘ + floating tip). The time already rides the right edge, so this line
    // omits it (withTime:false). CSS (.session-item__meta--hover) does the reveal.
    if (compact && !active) {
      const hoverMeta = this.buildMeta(s, true, { withTime: false });
      if (hoverMeta) {
        hoverMeta.classList.add("session-item__meta--hover");
        item.appendChild(hoverMeta);
      }
    }

    // Note line — only in the Needs-you section. Shows the agent's ask; falls
    // back to a state phrase when the hook didn't push notify text. 2-line
    // clamp keeps the framed row tight.
    if (showNote) {
      const level =
        s.notification?.level ?? (s.agentState === "permission" ? "error" : "warn");
      const text =
        s.notification?.text ??
        (s.agentState === "permission"
          ? "Needs your permission"
          : "Waiting for your input");
      const note = document.createElement("span");
      note.className = `session-item__note session-item__note--${level}`;
      const txt = document.createElement("span");
      txt.className = "session-item__note-text";
      txt.textContent = text;
      note.appendChild(txt);
      item.appendChild(note);
    }

    const close = document.createElement("button");
    close.type = "button";
    close.className = "session-item__close";
    close.title = "Close session";
    close.setAttribute("aria-label", `Close ${s.title}`);
    close.textContent = "✕";
    // Closing a session stops its panes but ARCHIVES the layout to "Recently
    // closed", so it's recoverable. Still a confirm — it tears down running
    // shells — but no longer the scary "can't be recovered" copy.
    close.addEventListener("click", async (ev) => {
      ev.stopPropagation();
      const panes = s.paneCount === 1 ? "1 pane" : `${s.paneCount} panes`;
      // A worktree tab owns a real folder on disk. Closing keeps it (so Recently
      // closed can restore straight back into it); the checkbox is how you say
      // "and reclaim the folder too". The branch survives either way, and the
      // copy says so — that's the reassuring part, because the commits ARE the
      // work and no close should be able to take them.
      const wt = s.worktreeBranch;
      const { ok, optionChecked } = await confirmWithOption({
        title: `Close ${s.title}?`,
        body: wt
          ? `Stops this session's ${panes}. Its worktree is kept, so you can reopen it from Recently closed.`
          : `Stops this session's ${panes}. You can reopen it from Recently closed.`,
        confirmLabel: "Close session",
        cancelLabel: "Keep open",
        danger: true,
        option: wt
          ? {
              label: "Also delete its worktree folder",
              hint: `Keeps the branch ${wt}, but the tab can't be reopened.`,
            }
          : undefined,
      });
      if (ok) send({ type: "session.close", id: s.id, removeWorktree: optionChecked });
    });
    item.appendChild(close);

    item.addEventListener("click", () => {
      if (!active) send({ type: "session.select", id: s.id });
    });

    return item;
  }

  /**
   * The row's metrics line, or null when the session has none to show.
   *   working → "▸ what it's doing · 2m"
   *   done    → "finished · 2m ago · +A −D · ⎇ branch ↑N"
   *   else    → "⎇ branch · :ports"     (dormant / needs-you keep code context)
   * Session mode renders it under every title (flex-wrap, may take two muted
   * lines). Compact (projects) mode renders ONE ellipsizing line — inline on
   * the active tab, inside the hover-ⓘ tip for the rest — with a timer glyph
   * standing in for the word "finished".
   */
  private buildMeta(s: SessionView, compact: boolean, opts?: { withTime?: boolean }): HTMLElement | null {
    // withTime:false drops the leading elapsed/age item — used by the inline
    // hover-meta, where the time already rides the row's right edge.
    const withTime = opts?.withTime ?? true;
    const metaItems: Array<{
      text: string;
      alert?: boolean;
      turnStart?: number;
      since?: number;
      sinceTimer?: number;   // compact form of `since`: clock glyph + bare count-up
      diff?: { added: number; deleted: number };
      ahead?: number;
    }> = [];
    // The unpushed-commit chip rides just after the branch. Rendered as its own
    // accent-colored, interactive span (hover → recap, click → details), so it
    // can't be a plain text suffix anymore.
    const aheadItem = s.ahead > 0 ? { text: "", ahead: s.ahead } : null;

    if (s.agentState === "working") {
      metaItems.push({ text: `▸ ${s.activityDetail || "working"}`, turnStart: withTime ? s.turnStartMs : undefined });
    } else if (s.agentState === "done") {
      // Lead with live "finished · 2m ago" so the freshness reads first — this
      // is the "your move" section, and how long it's been waiting on you is
      // the most useful signal. Falls back to nothing if the turn-end wasn't
      // stamped (older sessions). Compact rows spend a glyph instead of the
      // word: "⏱ 47s" counting up says the same in a third of the width.
      if (withTime && s.doneAtMs > 0)
        metaItems.push(
          compact ? { text: "", sinceTimer: s.doneAtMs } : { text: "finished", since: s.doneAtMs }
        );
      // Color-coded diff (+adds green / −dels red) reads at a glance vs plain
      // text. Rendered as sub-spans in the loop below.
      if (s.linesAdded || s.linesDeleted)
        metaItems.push({ text: "", diff: { added: s.linesAdded, deleted: s.linesDeleted } });
      if (s.branch) metaItems.push({ text: `⎇ ${s.branch}` });
      if (aheadItem) metaItems.push(aheadItem);
    } else {
      // dormant idle / needs-you: branch + unpushed + dev-server ports.
      if (s.branch) metaItems.push({ text: `⎇ ${s.branch}` });
      if (aheadItem) metaItems.push(aheadItem);
      for (const p of s.ports ?? []) metaItems.push({ text: `:${p}` });
    }

    // Pane breakdown, appended for any multi-pane session.
    if (s.paneCount > 1) {
      const parts: string[] = [`${s.paneCount} panes`];
      if (s.waitingCount > 0) parts.push(`${s.waitingCount} waiting`);
      else if (s.workingCount > 0) parts.push(`${s.workingCount} working`);
      metaItems.push({ text: parts.join(" · "), alert: s.waitingCount > 0 });
    }

    // Compact rows ellipsize as ONE line, shedding from the right — so the
    // branch chip moves to the end. It's the least differentiating item in a
    // project group (the tabs usually share it), and clipping it beats
    // clipping ↑N or the pane breakdown.
    if (compact) {
      const bi = metaItems.findIndex((mi) => mi.text.startsWith("⎇"));
      if (bi >= 0) metaItems.push(metaItems.splice(bi, 1)[0]);
    }

    if (!metaItems.length) return null;

    const meta = document.createElement("span");
    meta.className =
      "session-item__meta" + (compact ? " session-item__meta--compact" : "");
    for (const mi of metaItems) {
      // Compact meta is ONE block-flow line (so it can ellipsize as a
      // whole); flex gap doesn't apply there, so separate items with
      // literal dots instead.
      if (compact && meta.childNodes.length) meta.append(" · ");
      if (mi.ahead) {
        // Accent-blue, interactive "↑N ready to push" chip (shared builder —
        // the project header wears the same chip for the group's sum).
        meta.appendChild(aheadChip(mi.ahead, aheadPaneId(s)));
        continue;
      }
      const span = document.createElement("span");
      span.className =
        "session-item__meta-item" + (mi.alert ? " session-item__meta-item--alert" : "");
      if (mi.sinceTimer) {
        // Clock glyph + bare count-up ("47s", "2m") — the compact stand-in
        // for "finished · 47s ago". Same shared 1Hz ticker.
        span.classList.add("session-item__meta-item--timer");
        span.title = "Time since the agent finished";
        span.appendChild(timerIcon());
        span.appendChild(elapsedSpan(mi.sinceTimer));
        meta.appendChild(span);
        continue;
      }
      if (mi.diff) {
        // Colored +adds / −dels, reusing the footer's diff palette classes.
        if (mi.diff.added) {
          const add = document.createElement("span");
          add.className = "diff-add";
          add.textContent = `+${mi.diff.added}`;
          span.appendChild(add);
        }
        if (mi.diff.deleted) {
          if (mi.diff.added) span.append(" ");
          const del = document.createElement("span");
          del.className = "diff-del";
          del.textContent = `−${mi.diff.deleted}`;
          span.appendChild(del);
        }
      } else {
        span.textContent = mi.text;
      }
      // Live "· 2m" elapsed appended to the working item; the ticker only
      // rewrites the inner span, leaving the action text untouched.
      if (mi.turnStart && mi.turnStart > 0) {
        span.append(" · ");
        span.appendChild(elapsedSpan(mi.turnStart));
      }
      // Live "finished · 2m ago" on done rows — same ticker, relative form.
      if (mi.since && mi.since > 0) {
        span.append(" · ");
        span.appendChild(agoSpan(mi.since));
      }
      meta.appendChild(span);
    }
    return meta;
  }
}

/** Single-stroke clock glyph (Fluent/Lucide family) — the compact rows'
 *  stand-in for the word "finished"; reads as "time since". */
function timerIcon(): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("class", "session-item__timer-glyph");
  svg.setAttribute("width", "10");
  svg.setAttribute("height", "10");
  svg.setAttribute("viewBox", "0 0 12 12");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.2");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  const face = document.createElementNS(ns, "circle");
  face.setAttribute("cx", "6");
  face.setAttribute("cy", "6");
  face.setAttribute("r", "4.5");
  const hands = document.createElementNS(ns, "path");
  hands.setAttribute("d", "M6 3.6 V6 L7.7 7.1");
  svg.append(face, hands);
  return svg;
}

/** Single-stroke "rotate-ccw" restore glyph (Fluent/Lucide family). */
function restoreIcon(): SVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("width", "13");
  svg.setAttribute("height", "13");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  const poly = document.createElementNS(ns, "polyline");
  poly.setAttribute("points", "1 4 1 10 7 10");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", "M3.51 15a9 9 0 1 0 2.13-9.36L1 10");
  svg.append(poly, path);
  return svg;
}
