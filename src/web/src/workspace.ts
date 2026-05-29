// The workspace renders the active session's pane tree as nested flex
// containers (one per split). Reconciliation reuses existing Pane instances
// (and their attached xterm.js + scrollback) by paneId across state pushes,
// so a redraw after `session.select` or `pane.split` doesn't blow away
// terminal state.

import type { PaneTreeView, SessionView } from "./bridge.js";
import { Pane, DEFAULT_FONT_SIZE } from "./pane.js";
import { UrlPane } from "./url-pane.js";
import { PANE_LEAVE_MS, SESSION_SWAP_MS } from "./anim.js";

type LeafPane = Pane | UrlPane;

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
  private readonly panes = new Map<string, LeafPane>();
  // Bytes that arrive before the matching Pane is attached.
  private readonly pendingBytes = new Map<string, string[]>();
  private currentSessionId: string | null = null;
  private activePaneId: string | null = null;
  // Default font size for newly-created Panes. Updated on each state push
  // from the host's persisted prefs, so a freshly-split pane opens at the
  // user's saved size instead of the hardcoded default.
  private defaultFontSize: number = DEFAULT_FONT_SIZE;
  // Signature of the last rendered tree (paneIds + split shape). When the
  // next render comes in with the same signature, we skip the DOM
  // rebuild entirely — only setActive runs. Without this, every focus
  // change triggers replaceChildren which interrupts the focus-fade CSS
  // transition (DOM removal resets transitions on re-insert).
  private lastTreeSignature: string | null = null;

  constructor(rootEl: HTMLElement) {
    this.root = rootEl;
  }

  /** Reconcile the workspace to the active session's pane tree. */
  render(activeSession: SessionView | null, activePaneId: string | null) {
    if (!activeSession) {
      this.clear();
      this.activePaneId = null;
      return;
    }

    // Session swap → fade out, swap content, fade in. Same-session updates
    // are incremental and never trigger the fade so focus/state changes
    // don't blink. We re-enter the function with the same args after the
    // fade-out timeout has cleared the old content.
    if (this.currentSessionId && this.currentSessionId !== activeSession.id) {
      this.animateSwap(activeSession, activePaneId);
      return;
    }

    // On session change, tear down old panes (different paneIds, no reuse).
    // Within the same session, we keep Pane instances around so scrollback
    // and cursor position survive a split / close re-render.
    if (this.currentSessionId !== activeSession.id) {
      this.disposeAllPanes();
      this.lastTreeSignature = null;
      this.currentSessionId = activeSession.id;
    } else {
      // Identify panes that are leaving (in our map, not in the new tree).
      // If any, run a fade-out + scale animation BEFORE committing the
      // DOM rebuild — otherwise the closing pane snaps to gone and its
      // siblings collapse the space instantly.
      const keep = new Set<string>();
      this.collectLeafIds(activeSession.rootPane, keep);
      const leaving: string[] = [];
      for (const id of [...this.panes.keys()]) {
        if (!keep.has(id)) leaving.push(id);
      }
      if (leaving.length > 0) {
        for (const id of leaving) {
          const pane = this.panes.get(id);
          if (!pane) continue;
          pane.element.classList.add("pane--leaving");
          // For URL panes: tear down the host-side WebView2 immediately
          // so the child window closes at the START of the fade, in sync
          // with the placeholder div's opacity transition. Without this
          // the WebView2 sits full-opacity over a fading placeholder and
          // the close feels clunky.
          if (pane instanceof UrlPane) pane.beginLeaving();
        }
        // After the animation, dispose the leaving panes + commit the
        // tree rebuild. Re-entering render() with the latest state push
        // would race; we just inline the commit here.
        const ses = activeSession;
        const active = activePaneId;
        window.setTimeout(() => {
          for (const id of leaving) {
            this.panes.get(id)?.dispose();
            this.panes.delete(id);
          }
          this.commitRender(ses, active);
        }, PANE_LEAVE_MS);
        return;
      }
    }

    this.commitRender(activeSession, activePaneId);
  }

  /** Final commit step shared by the close-animation path and the
   *  no-removal path. Pushes per-pane state and rebuilds the DOM only
   *  when the tree shape changed. */
  private commitRender(activeSession: SessionView, activePaneId: string | null) {
    // Push per-pane state to every existing Pane on every state message.
    // This is cheap (just attribute/text updates) and never re-mounts
    // DOM, so in-flight CSS transitions survive. The signature-gated
    // DOM rebuild below only runs when the tree SHAPE changes (split,
    // close, session swap).
    this.applyState(activeSession.rootPane);

    // Only rebuild the DOM when the tree shape actually changed. A pure
    // focus or state change pushes the same shape — we MUST NOT re-mount
    // the panes in that case (removing+re-inserting a DOM element cancels
    // in-flight CSS transitions, killing the focus-fade effect).
    const signature = treeSignature(activeSession.rootPane);
    const rebuilt = signature !== this.lastTreeSignature;
    if (rebuilt) {
      this.root.replaceChildren();
      this.renderTree(activeSession.rootPane, this.root);
      this.lastTreeSignature = signature;
    }

    // Apply active marker after the DOM is up. Only steal keyboard focus when
    // the tree was rebuilt (split/close re-created the element) — a pure
    // state push must not refocus, or it fights the user's clicks.
    this.setActive(activePaneId, rebuilt);
  }

  /** Crossfade between sessions. Fade-out the current contents, swap them
   *  for the new session's panes, fade back in. Cheap visual signal that
   *  "you switched contexts" — same family as the focus-fade and 200ms
   *  is the standard chrome transition duration in tokens.css. */
  private animateSwap(activeSession: SessionView, activePaneId: string | null) {
    this.root.classList.add("workspace--switching");
    window.setTimeout(() => {
      this.disposeAllPanes();
      this.lastTreeSignature = null;
      this.currentSessionId = activeSession.id;
      this.root.replaceChildren();
      this.renderTree(activeSession.rootPane, this.root);
      this.applyState(activeSession.rootPane);
      this.lastTreeSignature = treeSignature(activeSession.rootPane);
      this.setActive(activePaneId, /* forceFocus (DOM rebuilt) */ true);
      // One rAF so the just-inserted DOM has a frame to lay out before
      // the opacity transition starts — otherwise the in-fade is skipped
      // (Chromium treats the freshly-inserted node as never having been
      // transparent and snaps it straight to 1).
      requestAnimationFrame(() => {
        this.root.classList.remove("workspace--switching");
      });
    }, SESSION_SWAP_MS);
  }

  /** Walk the tree and push the latest per-leaf state into existing Pane
   *  instances. Called on every render before the DOM-rebuild check so
   *  state updates are visible even when structure didn't change. */
  private applyState(node: PaneTreeView) {
    if (node.kind === "leaf") {
      const pane = this.panes.get(node.paneId);
      if (pane) pane.applyLeafView(node);
      return;
    }
    for (const c of node.children) this.applyState(c);
  }

  feed(paneId: string, b64: string) {
    const pane = this.panes.get(paneId);
    if (pane) { pane.feed(b64); return; }
    const queue = this.pendingBytes.get(paneId) ?? [];
    queue.push(b64);
    this.pendingBytes.set(paneId, queue);
  }

  notifyExit(paneId: string, code: number) {
    this.panes.get(paneId)?.notifyExit(code);
  }

  /** Update the active-pane visual marker, and move keyboard focus ONLY when
   *  the active pane actually changed or the DOM was just rebuilt
   *  (`forceFocus`).
   *
   *  This method runs on every state push, and state pushes fire on every
   *  agent status/notify/meta — several times a second when agents are busy.
   *  The previous version re-focused the terminal unconditionally on every
   *  call, which stole keyboard focus back from whatever pane the user had
   *  clicked into, wiped in-progress text selections, and made focus appear
   *  to "jump to the wrong pane". Gating focus on an actual change kills that
   *  storm while still focusing correctly on click / split / session swap. */
  setActive(paneId: string | null, forceFocus = false) {
    const changed = paneId !== this.activePaneId;
    if (changed && this.activePaneId) {
      this.panes.get(this.activePaneId)?.setActive(false);
    }
    this.activePaneId = paneId;
    if (!paneId) return;
    const pane = this.panes.get(paneId);
    if (changed || forceFocus) {
      pane?.setActive(true);
      // requestAnimationFrame ensures the DOM is settled before focus.
      requestAnimationFrame(() => pane?.focus());
    }
  }

  getActivePaneId(): string | null { return this.activePaneId; }

  /** The root DOM element of a mounted pane, or null when that pane isn't in
   *  the currently-rendered session. Used to anchor the notify toast to the
   *  pane that fired it. */
  paneElement(paneId: string): HTMLElement | null {
    return this.panes.get(paneId)?.element ?? null;
  }

  /** Returns the Pane currently marked active, or null. Used by the
   *  font-size shortcuts in main.ts; only terminal Panes will actually
   *  resize, UrlPane.changeFontSize is a no-op. */
  /** Apply user prefs from a state push: store as the default for future
   *  new panes, and update existing terminal panes that don't already
   *  match. Idempotent — Pane.setFontSize skips the refit on a no-op. */
  applyPrefs(prefs: { fontSize: number }) {
    this.defaultFontSize = prefs.fontSize;
    for (const pane of this.panes.values()) {
      if (pane instanceof Pane) pane.setFontSize(prefs.fontSize);
    }
  }

  /** Ask every URL pane to re-emit its layout. Called by main.ts on
   *  ui.urlpane.relayout (fired by host on window move / resize). */
  nudgeUrlPanes() {
    for (const pane of this.panes.values()) {
      if (pane instanceof UrlPane) pane.forceRefit();
    }
  }

  getActivePane(): LeafPane | null {
    return this.activePaneId
      ? this.panes.get(this.activePaneId) ?? null
      : null;
  }

  private clear() {
    this.disposeAllPanes();
    this.root.replaceChildren();
    this.currentSessionId = null;
  }

  private disposeAllPanes() {
    for (const p of this.panes.values()) p.dispose();
    this.panes.clear();
  }

  private collectLeafIds(node: PaneTreeView, out: Set<string>) {
    if (node.kind === "leaf") { out.add(node.paneId); return; }
    for (const c of node.children) this.collectLeafIds(c, out);
  }

  private renderTree(node: PaneTreeView, host: HTMLElement) {
    if (node.kind === "leaf") {
      let pane = this.panes.get(node.paneId);
      if (!pane) {
        // URL leaves render as iframe-backed UrlPane; everything else is
        // a terminal-backed Pane. The discriminator lives in the host's
        // PaneTreeView (set when OnPaneSplit was passed a `url`).
        pane = node.url
          ? new UrlPane(node.paneId, node.name, node.url)
          : new Pane(node.paneId, node.name, this.defaultFontSize);
        this.panes.set(node.paneId, pane);
        pane.attach(host);
        // Push initial state so the freshly-created pane header reflects
        // whatever the host already knows (e.g. after a reattach).
        pane.applyLeafView(node);
        // Fade in. The class triggers the @keyframes pane-enter animation
        // once (CSS animations replay on next add/remove of the class).
        // Cleaned up via animationend so we don't accumulate classes.
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
        // Pane already exists from a previous render (split changed the
        // tree shape but not this leaf). Reattach to the new parent.
        host.appendChild(pane.element);
        pane.setName(node.name);
        // The new parent (a split container) gives the pane a different
        // size than before. ResizeObserver doesn't always fire on
        // reparent if contentRect ends up the same numerically, so kick
        // a fresh fit so the host learns about the new dimensions and
        // PowerShell gets ResizePseudoConsole at the right size.
        pane.forceRefit();
      }
      return;
    }
    const splitEl = document.createElement("div");
    splitEl.className = `split split--${node.orientation}`;
    host.appendChild(splitEl);
    for (const child of node.children) this.renderTree(child, splitEl);
  }
}
