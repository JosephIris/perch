// The workspace renders the active session's pane tree as nested flex
// containers (one per split). Each SESSION gets its own "stage" — a container
// DIV that stays mounted across session switches and is merely hidden when
// inactive. Switching sessions toggles which stage is visible; it does NOT
// dispose the panes. That's what preserves each session's xterm scrollback,
// cursor position, and live output when you tab away and come back — the bug
// where "scrolling history dies after switching tabs" was disposeAllPanes()
// destroying every Terminal (and its 10k-line buffer) on every swap.
//
// Terminal panes stay alive while their session is hidden and keep receiving
// PTY output (so you see what happened while you were away, with full
// history). URL panes are the exception: their WebView2 lives in a native
// child window that ignores DOM `display:none`, so a hidden session's URL
// pane would float over the visible one — we tear those down on hide and let
// them rebuild (page reload, no scrollback to lose) when the session returns.

import type { PaneTreeView, SessionView } from "./bridge.js";
import { send } from "./bridge.js";
import { Pane, DEFAULT_FONT_SIZE } from "./pane.js";
import { UrlPane } from "./url-pane.js";
import { PANE_LEAVE_MS } from "./anim.js";
import { showPaneChooser } from "./pane-chooser.js";

type LeafPane = Pane | UrlPane;

/** Drop zone within a target pane during a drag-to-rearrange. */
type Edge = "left" | "right" | "top" | "bottom" | "center";

/** One mounted session: its container DIV (hidden when inactive), the panes
 *  keyed by id (reused across renders to preserve terminal state), and the
 *  last tree signature for the DOM-rebuild gate. */
interface Stage {
  readonly sessionId: string;
  readonly container: HTMLElement;
  readonly panes: Map<string, LeafPane>;
  signature: string | null;
}

/** Stable string for a pane tree's SHAPE — leaf paneIds + split
 *  orientations, in document order. Used to skip the DOM rebuild on
 *  no-op renders (pure focus changes). Does NOT include the active-pane
 *  marker or any other state that toggles via setActive. */
function treeSignature(node: PaneTreeView): string {
  if (node.kind === "leaf") return `L:${node.paneId}:${node.url ?? ""}`;
  return `S(${node.orientation}:${node.children.map(treeSignature).join(",")})`;
}

export class Workspace {
  private readonly root: HTMLElement;
  // One stage per session id. Stages persist across switches; a stage is
  // disposed only when its session is closed (gone from the state push).
  private readonly stages = new Map<string, Stage>();
  // Bytes that arrive before the matching Pane is attached.
  private readonly pendingBytes = new Map<string, string[]>();
  private activeSessionId: string | null = null;
  private activePaneId: string | null = null;
  // Default font size for newly-created Panes. Updated on each state push
  // from the host's persisted prefs, so a freshly-split pane opens at the
  // user's saved size instead of the hardcoded default.
  private defaultFontSize: number = DEFAULT_FONT_SIZE;

  // Drag-to-rearrange state. Set on a pane-header dragstart, cleared on
  // dragend/drop. The shared drop overlay is a single fixed-position element
  // that highlights where the dragged pane would land.
  private draggingPaneId: string | null = null;
  private readonly dropOverlay: HTMLElement;
  // Pane ids currently showing the new-pane chooser, so a re-posted
  // pane.chooser doesn't stack a second overlay. Cleared when the pick resolves.
  private readonly choosersOpen = new Set<string>();

  constructor(rootEl: HTMLElement) {
    this.root = rootEl;
    this.dropOverlay = document.createElement("div");
    this.dropOverlay.className = "drop-overlay";
    this.dropOverlay.style.display = "none";
    document.body.appendChild(this.dropOverlay);
  }

  /** Reconcile the workspace to the current set of sessions and the active
   *  one. Sessions that vanished from the list are closed → their stage is
   *  disposed. The active session's stage is shown and reconciled; the rest
   *  stay mounted but hidden. */
  render(
    sessions: SessionView[],
    activeSessionId: string | null,
    activePaneId: string | null
  ) {
    // 1. Dispose stages for sessions that no longer exist (closed). This is
    //    the ONLY path that disposes panes now — a switch never does.
    const live = new Set(sessions.map((s) => s.id));
    for (const [sid, stage] of [...this.stages]) {
      if (!live.has(sid)) {
        for (const p of stage.panes.values()) p.dispose();
        stage.container.remove();
        this.stages.delete(sid);
      }
    }

    const active = activeSessionId
      ? sessions.find((s) => s.id === activeSessionId) ?? null
      : null;

    if (!active) {
      // No active session — hide every stage but keep them mounted.
      for (const st of this.stages.values()) st.container.style.display = "none";
      this.activeSessionId = null;
      return;
    }

    const switching =
      this.activeSessionId !== null && this.activeSessionId !== active.id;

    // 2. Get or create the active session's stage.
    let stage = this.stages.get(active.id);
    const isNew = !stage;
    if (!stage) {
      const container = document.createElement("div");
      container.className = "workspace__stage";
      this.root.appendChild(container);
      stage = { sessionId: active.id, container, panes: new Map(), signature: null };
      this.stages.set(active.id, stage);
    }

    // 3. When leaving a session, tear down its URL panes (native child
    //    windows ignore display:none and would float over the new session).
    //    Terminal panes are left untouched — that's the whole point.
    if (switching && this.activeSessionId) {
      const leaving = this.stages.get(this.activeSessionId);
      if (leaving) this.hideStage(leaving);
    }

    // 4. Show the active stage, hide the rest.
    for (const [sid, st] of this.stages) {
      st.container.style.display = sid === active.id ? "" : "none";
    }

    // 5. Reconcile the active session into its stage and mark it active.
    this.reconcile(stage, active, activePaneId, switching || isNew);
    this.activeSessionId = active.id;

    // 6. Gentle fade-in when this stage just became visible.
    if (switching || isNew) this.fadeInStage(stage);
  }

  /** Hide a stage that's being switched away from: dispose its URL panes so
   *  their native WebView2 windows close (and drop them from the map so they
   *  rebuild on return). Terminals stay alive. */
  private hideStage(stage: Stage) {
    let removedUrlPane = false;
    for (const [id, pane] of [...stage.panes]) {
      if (pane instanceof UrlPane) {
        pane.dispose();
        stage.panes.delete(id);
        removedUrlPane = true;
      }
    }
    // Force a rebuild on next show so the disposed URL pane's DOM is recreated
    // (renderTree will spawn a fresh WebView2 for it).
    if (removedUrlPane) stage.signature = null;
  }

  private fadeInStage(stage: Stage) {
    const el = stage.container;
    el.classList.remove("workspace__stage--in");
    // Force a reflow so re-adding the class restarts the animation.
    void el.offsetWidth;
    el.classList.add("workspace__stage--in");
    el.addEventListener(
      "animationend",
      () => el.classList.remove("workspace__stage--in"),
      { once: true }
    );
  }

  /** Reconcile a session's tree into its stage. Handles the in-session pane-
   *  close fade-out, the signature-gated DOM rebuild, per-pane state push,
   *  and the active marker. `forceFocus` re-focuses + refits (used when the
   *  stage just became visible). */
  private reconcile(
    stage: Stage,
    session: SessionView,
    activePaneId: string | null,
    forceFocus: boolean
  ) {
    // Panes in the map but not in the new tree are leaving (closed).
    const keep = new Set<string>();
    this.collectLeafIds(session.rootPane, keep);
    const leaving: string[] = [];
    for (const id of [...stage.panes.keys()]) {
      if (!keep.has(id)) leaving.push(id);
    }

    if (leaving.length > 0 && !forceFocus) {
      // Animate the close only when the session is already visible. (On a
      // switch we don't fade closes that happened while the session was
      // hidden — just commit.)
      for (const id of leaving) {
        const pane = stage.panes.get(id);
        if (!pane) continue;
        pane.element.classList.add("pane--leaving");
        if (pane instanceof UrlPane) pane.beginLeaving();
      }
      window.setTimeout(() => {
        for (const id of leaving) {
          stage.panes.get(id)?.dispose();
          stage.panes.delete(id);
        }
        this.commit(stage, session, activePaneId, false);
      }, PANE_LEAVE_MS);
      return;
    }

    // Switch path (or no leavers): dispose any leavers without animation,
    // then commit.
    for (const id of leaving) {
      stage.panes.get(id)?.dispose();
      stage.panes.delete(id);
    }
    this.commit(stage, session, activePaneId, forceFocus);
  }

  /** Final commit: push per-pane state, rebuild the stage DOM only when the
   *  tree shape changed, set the active marker, and (when newly visible)
   *  refit so the xterm viewport picks up the real size. */
  private commit(
    stage: Stage,
    session: SessionView,
    activePaneId: string | null,
    forceFocus: boolean
  ) {
    // Cheap attribute/text updates to every existing pane — never re-mounts
    // DOM, so in-flight CSS transitions survive.
    this.applyState(stage, session.rootPane);

    // Only rebuild the DOM when the tree shape actually changed. A pure focus
    // or state change pushes the same shape — re-mounting would cancel
    // in-flight CSS transitions (killing the focus-fade).
    const signature = treeSignature(session.rootPane);
    const rebuilt = signature !== stage.signature;
    if (rebuilt) {
      stage.container.replaceChildren();
      this.renderTree(stage, session.rootPane, stage.container);
      stage.signature = signature;
    }

    this.setActive(stage, activePaneId, rebuilt || forceFocus);

    // When a stage becomes visible again its panes may have been measured at
    // zero size while hidden (or the window resized in the meantime) — refit
    // so cols/rows and the scroll viewport match the real geometry.
    if (forceFocus) {
      for (const p of stage.panes.values()) p.forceRefit();
    }
  }

  /** Walk the tree and push the latest per-leaf state into existing Pane
   *  instances of this stage. */
  private applyState(stage: Stage, node: PaneTreeView) {
    if (node.kind === "leaf") {
      stage.panes.get(node.paneId)?.applyLeafView(node);
      return;
    }
    for (const c of node.children) this.applyState(stage, c);
  }

  feed(paneId: string, b64: string) {
    const pane = this.findPane(paneId);
    if (pane) { pane.feed(b64); return; }
    const queue = this.pendingBytes.get(paneId) ?? [];
    queue.push(b64);
    this.pendingBytes.set(paneId, queue);
  }

  notifyExit(paneId: string, code: number) {
    this.findPane(paneId)?.notifyExit(code);
  }

  /** Find a pane by id across ALL stages — background sessions' panes are
   *  alive and still receive output, so feed/exit must reach them too. */
  private findPane(paneId: string): LeafPane | null {
    for (const st of this.stages.values()) {
      const p = st.panes.get(paneId);
      if (p) return p;
    }
    return null;
  }

  /** Update the active-pane visual marker, and move keyboard focus ONLY when
   *  the active pane actually changed or the stage was just rebuilt/shown
   *  (`forceFocus`).
   *
   *  Runs on every state push (several times a second when agents are busy).
   *  Gating focus on an actual change avoids stealing keyboard focus back
   *  from whatever pane the user clicked into and wiping text selections. */
  private setActive(stage: Stage, paneId: string | null, forceFocus = false) {
    const changed = paneId !== this.activePaneId;
    if (changed && this.activePaneId) {
      // The previously-active pane may live in another (now-hidden) stage if
      // we just switched sessions — find it anywhere.
      this.findPane(this.activePaneId)?.setActive(false);
    }
    this.activePaneId = paneId;
    if (!paneId) return;
    const pane = stage.panes.get(paneId);
    if (changed || forceFocus) {
      pane?.setActive(true);
      // requestAnimationFrame ensures the DOM is settled before focus.
      requestAnimationFrame(() => pane?.focus());
    }
  }

  getActivePaneId(): string | null { return this.activePaneId; }

  /** The root DOM element of a mounted, VISIBLE pane, or null. Used to anchor
   *  the notify toast — only the active session's panes are on screen, so a
   *  background pane returns null and the toast falls back to default
   *  positioning. */
  paneElement(paneId: string): HTMLElement | null {
    const st = this.activeSessionId ? this.stages.get(this.activeSessionId) : null;
    return st?.panes.get(paneId)?.element ?? null;
  }

  /** Show the in-pane "new pane" chooser inside a freshly-split terminal
   *  pane, then ship the user's pick to the host (which has parked this pane's
   *  shell spawn until pane.chooser.choose answers). No-op if the pane is gone,
   *  is a URL pane, or is already showing the chooser (a re-posted
   *  pane.chooser must not stack a second overlay). */
  showPaneChooser(opts: { paneId: string; cwd: string; agentType: string; defaultCwd: string }) {
    const pane = this.findPane(opts.paneId);
    if (!pane || pane instanceof UrlPane) return;
    if (this.choosersOpen.has(opts.paneId)) return;
    this.choosersOpen.add(opts.paneId);
    void showPaneChooser(pane.element, opts).then((choice) => {
      this.choosersOpen.delete(opts.paneId);
      send({ type: "pane.chooser.choose", paneId: opts.paneId, choice });
    });
  }

  /** Apply user prefs from a state push: store as the default for future new
   *  panes, and update existing terminal panes (across all stages). */
  applyPrefs(prefs: { fontSize: number }) {
    this.defaultFontSize = prefs.fontSize;
    for (const st of this.stages.values()) {
      for (const pane of st.panes.values()) {
        if (pane instanceof Pane) pane.setFontSize(prefs.fontSize);
      }
    }
  }

  /** Ask every URL pane to re-emit its layout. Called on
   *  ui.urlpane.relayout (host window move / resize). Only the active stage's
   *  URL panes have live WebView2 windows, but nudging all is harmless. */
  nudgeUrlPanes() {
    for (const st of this.stages.values()) {
      for (const pane of st.panes.values()) {
        if (pane instanceof UrlPane) pane.forceRefit();
      }
    }
  }

  getActivePane(): LeafPane | null {
    const st = this.activeSessionId ? this.stages.get(this.activeSessionId) : null;
    return this.activePaneId ? st?.panes.get(this.activePaneId) ?? null : null;
  }

  private collectLeafIds(node: PaneTreeView, out: Set<string>) {
    if (node.kind === "leaf") { out.add(node.paneId); return; }
    for (const c of node.children) this.collectLeafIds(c, out);
  }

  /** Render a subtree under `host` and return the top element for `node`
   *  (the pane element for a leaf, the split container for a split) so the
   *  caller can set its flex-grow weight. */
  private renderTree(stage: Stage, node: PaneTreeView, host: HTMLElement): HTMLElement {
    if (node.kind === "leaf") {
      let pane = stage.panes.get(node.paneId);
      if (!pane) {
        // URL leaves render as iframe-backed UrlPane; everything else is a
        // terminal-backed Pane. The discriminator lives in the host's
        // PaneTreeView (set when OnPaneSplit was passed a `url`).
        pane = node.url
          ? new UrlPane(node.paneId, node.name, node.url)
          : new Pane(node.paneId, node.name, this.defaultFontSize);
        stage.panes.set(node.paneId, pane);
        pane.attach(host);
        // Push initial state so the freshly-created pane header reflects
        // whatever the host already knows.
        pane.applyLeafView(node);
        // Make the pane a drag-to-rearrange source (header) + drop target.
        this.wirePaneDnd(pane);
        // Fade in. The class triggers the @keyframes pane-enter animation
        // once; cleaned up via animationend.
        const el = pane.element;
        el.classList.add("pane--entering");
        el.addEventListener(
          "animationend",
          () => el.classList.remove("pane--entering"),
          { once: true },
        );
        const queued = this.pendingBytes.get(node.paneId);
        if (queued) {
          for (const b64 of queued) pane.feed(b64);
          this.pendingBytes.delete(node.paneId);
        }
      } else {
        // Pane already exists (split changed the tree shape, or the stage is
        // being re-shown). Reattach to the new parent — this MOVES the live
        // xterm element, preserving its buffer/scrollback.
        host.appendChild(pane.element);
        pane.setName(node.name);
        // ResizeObserver doesn't always fire on reparent if contentRect ends
        // up the same numerically, so kick a fresh fit.
        pane.forceRefit();
      }
      return pane.element;
    }
    const splitEl = document.createElement("div");
    splitEl.className = `split split--${node.orientation}`;
    splitEl.dataset.splitId = node.id;
    host.appendChild(splitEl);
    // Lay out children with a draggable gutter between each adjacent pair.
    // Each child's flex-grow comes from its weight (default 1 → even), so a
    // resized layout is reapplied here on every rebuild; treeSignature omits
    // weight, so a pure weight change never triggers a rebuild.
    node.children.forEach((child, i) => {
      if (i > 0) splitEl.appendChild(this.makeGutter(node.orientation));
      const childEl = this.renderTree(stage, child, splitEl);
      childEl.style.flexGrow = String(child.weight ?? 1);
    });
    return splitEl;
  }

  /** Evenly distribute every pane in the active session: reset every split
   *  child's flex-grow to 1 (so each level splits its space equally) and
   *  persist each split via the same resizeSplit path the gutter drag uses.
   *  No DOM rebuild — we mutate the live layout in place; the per-pane
   *  ResizeObserver refits xterm as the flex boxes change. */
  distributeEven(): void {
    const st = this.activeSessionId ? this.stages.get(this.activeSessionId) : null;
    if (!st) return;
    const splits = st.container.querySelectorAll<HTMLElement>(".split");
    splits.forEach((splitEl) => {
      for (const c of Array.from(splitEl.children)) {
        if ((c as HTMLElement).classList.contains("split__gutter")) continue;
        (c as HTMLElement).style.flexGrow = "1";
      }
      this.persistSplitWeights(splitEl);
    });
  }

  // ---- Resize: draggable split gutters ---------------------------------

  /** A grab handle that lives in the gap between two split children. Dragging
   *  it rewrites the flex-grow of the two adjacent siblings. */
  private makeGutter(orientation: "h" | "v"): HTMLElement {
    const g = document.createElement("div");
    g.className = `split__gutter split__gutter--${orientation}`;
    g.addEventListener("pointerdown", (ev) =>
      this.beginGutterDrag(ev, g, orientation),
    );
    return g;
  }

  private beginGutterDrag(
    ev: PointerEvent,
    gutter: HTMLElement,
    orientation: "h" | "v",
  ) {
    if (ev.button !== 0) return;
    const prev = gutter.previousElementSibling as HTMLElement | null;
    const next = gutter.nextElementSibling as HTMLElement | null;
    const splitEl = gutter.parentElement;
    if (!prev || !next || !splitEl) return;
    ev.preventDefault();

    // "v" splits lay children side-by-side (drag along X); "h" stacks them
    // (drag along Y).
    const horizontal = orientation === "v";
    const start = horizontal ? ev.clientX : ev.clientY;
    const prevPx0 = horizontal ? prev.offsetWidth : prev.offsetHeight;
    const nextPx0 = horizontal ? next.offsetWidth : next.offsetHeight;
    const totalPx = prevPx0 + nextPx0;
    const prevGrow0 = parseFloat(prev.style.flexGrow || "1") || 1;
    const nextGrow0 = parseFloat(next.style.flexGrow || "1") || 1;
    const totalGrow = prevGrow0 + nextGrow0;
    const MIN_PX = 64; // don't let a pane be dragged smaller than this

    gutter.setPointerCapture(ev.pointerId);
    gutter.classList.add("split__gutter--dragging");

    const onMove = (e: PointerEvent) => {
      const pos = horizontal ? e.clientX : e.clientY;
      let prevPx = prevPx0 + (pos - start);
      prevPx = Math.max(MIN_PX, Math.min(totalPx - MIN_PX, prevPx));
      const prevGrow = (totalGrow * prevPx) / totalPx;
      prev.style.flexGrow = String(prevGrow);
      next.style.flexGrow = String(totalGrow - prevGrow);
      // The per-pane ResizeObserver refits xterm automatically as the flex
      // box changes — no manual fit needed here.
    };
    const onUp = () => {
      gutter.releasePointerCapture(ev.pointerId);
      gutter.classList.remove("split__gutter--dragging");
      gutter.removeEventListener("pointermove", onMove);
      gutter.removeEventListener("pointerup", onUp);
      gutter.removeEventListener("pointercancel", onUp);
      this.persistSplitWeights(splitEl);
    };
    gutter.addEventListener("pointermove", onMove);
    gutter.addEventListener("pointerup", onUp);
    gutter.addEventListener("pointercancel", onUp);
  }

  /** Read the current flex-grow of every pane/split child of `splitEl` (in
   *  order, skipping gutters) and ship them to the host to persist. */
  private persistSplitWeights(splitEl: HTMLElement) {
    const splitId = splitEl.dataset.splitId;
    if (!splitId) return;
    const weights: number[] = [];
    for (const c of Array.from(splitEl.children)) {
      if ((c as HTMLElement).classList.contains("split__gutter")) continue;
      weights.push(parseFloat((c as HTMLElement).style.flexGrow || "1") || 1);
    }
    send({ type: "pane.resizeSplit", splitId, weights, final: true });
  }

  // ---- Move: drag a pane header to rearrange ---------------------------

  /** Make `pane` a drag source (via its header) and a drop target (its whole
   *  body). Dropping on an edge re-splits the target; dropping center swaps. */
  private wirePaneDnd(pane: LeafPane) {
    const el = pane.element;
    const header = el.querySelector<HTMLElement>(".pane__header");
    if (header) {
      header.draggable = true;
      header.addEventListener("dragstart", (ev) => {
        this.draggingPaneId = pane.paneId;
        el.classList.add("pane--dragging");
        if (ev.dataTransfer) {
          ev.dataTransfer.effectAllowed = "move";
          ev.dataTransfer.setData("text/plain", pane.paneId);
        }
      });
      header.addEventListener("dragend", () => {
        this.draggingPaneId = null;
        el.classList.remove("pane--dragging");
        this.hideDropOverlay();
      });
    }

    el.addEventListener("dragover", (ev) => {
      if (!this.draggingPaneId) return;
      ev.preventDefault();
      if (ev.dataTransfer) ev.dataTransfer.dropEffect = "move";
      if (this.draggingPaneId === pane.paneId) { this.hideDropOverlay(); return; }
      this.showDropOverlay(el, this.computeEdge(el, ev));
    });
    el.addEventListener("drop", (ev) => {
      const src = this.draggingPaneId;
      if (!src) return;
      ev.preventDefault();
      this.hideDropOverlay();
      if (src === pane.paneId) return;
      send({ type: "pane.move", src, target: pane.paneId, edge: this.computeEdge(el, ev) });
    });
  }

  /** Which drop zone the pointer is in: a centered box is "center" (swap),
   *  otherwise the nearest edge. */
  private computeEdge(el: HTMLElement, ev: DragEvent): Edge {
    const r = el.getBoundingClientRect();
    const x = (ev.clientX - r.left) / r.width;
    const y = (ev.clientY - r.top) / r.height;
    if (Math.abs(x - 0.5) < 0.22 && Math.abs(y - 0.5) < 0.22) return "center";
    const d = { left: x, right: 1 - x, top: y, bottom: 1 - y };
    let edge: Edge = "left";
    let min = d.left;
    for (const k of ["right", "top", "bottom"] as const) {
      if (d[k] < min) { min = d[k]; edge = k; }
    }
    return edge;
  }

  private showDropOverlay(el: HTMLElement, edge: Edge) {
    const r = el.getBoundingClientRect();
    let { left, top, width, height } = { left: r.left, top: r.top, width: r.width, height: r.height };
    if (edge === "left") width = r.width / 2;
    else if (edge === "right") { left = r.left + r.width / 2; width = r.width / 2; }
    else if (edge === "top") height = r.height / 2;
    else if (edge === "bottom") { top = r.top + r.height / 2; height = r.height / 2; }
    const o = this.dropOverlay;
    o.style.display = "block";
    o.style.left = `${left}px`;
    o.style.top = `${top}px`;
    o.style.width = `${width}px`;
    o.style.height = `${height}px`;
  }

  private hideDropOverlay() {
    this.dropOverlay.style.display = "none";
  }
}
