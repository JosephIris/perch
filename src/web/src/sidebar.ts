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
import { elapsedSpan, agoSpan } from "./elapsed.js";
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

  constructor(listEl: HTMLElement, newSessionBtn: HTMLElement, closedEl: HTMLElement) {
    this.listEl = listEl;
    this.newSessionBtn = newSessionBtn;
    this.closedEl = closedEl;

    this.newSessionBtn.addEventListener("click", () => {
      send({ type: "session.new" });
    });

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

  render(
    sessions: SessionView[],
    activeId: string,
    closed: ClosedSessionView[] = [],
    projects: ProjectView[] = [],
    mode: SidebarMode = "sessions"
  ) {
    this.renderClosed(closed);
    if (mode === "projects") {
      this.listEl.replaceChildren(this.renderProjects(sessions, activeId, projects));
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
        this.projectHeader(project, tabs.length, collapsed, aggregateState(tabs), animate)
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
      const item = this.renderItem(s, s.id === activeId, needsNote);
      if (nested) {
        item.classList.add("session-item--nested");
        // The tab's color tag. Same hue the host set INSIDE the Claude session
        // via /color, so a tab looks the same from the sidebar and from its
        // prompt bar. Carried on the title (like the pane header does) rather
        // than as a second dot — the row already has a state dot, and two dots
        // saying different things would just be noise.
        const color = leaves(s.rootPane)[0]?.colorIndex;
        if (color != null) item.dataset.color = String(color);
      }
      list.appendChild(item);
    }
    return list;
  }

  private projectHeader(
    p: ProjectView,
    tabCount: number,
    collapsed: boolean,
    state: SessionView["agentState"],
    animate = false
  ): HTMLElement {
    const row = document.createElement("div");
    row.className = "project-header";
    row.dataset.projectId = p.id;

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

  private renderItem(
    s: SessionView,
    active: boolean,
    showNote: boolean
  ): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "session-item" + (active ? " session-item--active" : "");
    item.dataset.sessionId = s.id;

    // Status dot on the left. Color comes from CSS via [data-state="..."];
    // idle = hollow ring, working/waiting/permission = solid fill.
    const dot = document.createElement("span");
    dot.className = "session-item__dot";
    dot.dataset.state = s.agentState;
    item.appendChild(dot);

    // Primary line: title only. The shell rides in the footer; the row gives
    // the title the whole line for a single clean ellipsis.
    const primary = document.createElement("span");
    primary.className = "session-item__primary";
    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = s.title;
    primary.appendChild(title);
    item.appendChild(primary);

    // Secondary line(s): one state-aware signal, then pane breakdown. flex-wrap
    // lets a dense session spill onto a second muted line.
    //   working → "▸ what it's doing"
    //   done    → "+A −D · ⎇ branch ↑N"  (what it produced / what's unpushed)
    //   else    → "⎇ branch · :ports"     (dormant / needs-you keep code context)
    const metaItems: Array<{
      text: string;
      alert?: boolean;
      turnStart?: number;
      since?: number;
      diff?: { added: number; deleted: number };
      ahead?: number;
    }> = [];
    // The unpushed-commit chip rides just after the branch. Rendered as its own
    // accent-colored, interactive span (hover → recap, click → details), so it
    // can't be a plain text suffix anymore.
    const aheadItem = s.ahead > 0 ? { text: "", ahead: s.ahead } : null;

    if (s.agentState === "working") {
      metaItems.push({ text: `▸ ${s.activityDetail || "working"}`, turnStart: s.turnStartMs });
    } else if (s.agentState === "done") {
      // Lead with live "finished · 2m ago" so the freshness reads first — this
      // is the "your move" section, and how long it's been waiting on you is
      // the most useful signal. Falls back to nothing if the turn-end wasn't
      // stamped (older sessions).
      if (s.doneAtMs > 0) metaItems.push({ text: "finished", since: s.doneAtMs });
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

    if (metaItems.length) {
      const meta = document.createElement("span");
      meta.className = "session-item__meta";
      for (const mi of metaItems) {
        const span = document.createElement("span");
        span.className =
          "session-item__meta-item" + (mi.alert ? " session-item__meta-item--alert" : "");
        if (mi.ahead) {
          // Accent-blue, interactive "↑N ready to push" chip.
          span.classList.add("session-item__meta-item--ahead");
          span.textContent = `↑${mi.ahead}`;
          span.title = "Commits ready to push";
          const pid = aheadPaneId(s);
          if (pid) {
            attachCommitsHover(span, pid);
            span.addEventListener("click", (ev) => {
              // The whole row is a button that selects the session — keep the
              // click here from also navigating.
              ev.stopPropagation();
              openCommitsLightbox(pid);
            });
          }
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
      item.appendChild(meta);
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
