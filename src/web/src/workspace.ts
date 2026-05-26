// The workspace renders the active session's pane tree as nested flex
// containers (one per split). Reconciliation reuses existing Pane instances
// (and their attached xterm.js + scrollback) by paneId across state pushes,
// so a redraw after `session.select` or `pane.split` doesn't blow away
// terminal state.

import type { PaneTreeView, SessionView } from "./bridge.js";
import { Pane } from "./pane.js";

export class Workspace {
  private readonly root: HTMLElement;
  private readonly panes = new Map<string, Pane>();
  // Bytes that arrive before the matching Pane is attached.
  private readonly pendingBytes = new Map<string, string[]>();
  private currentSessionId: string | null = null;
  private activePaneId: string | null = null;

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

    // On session change, tear down old panes (different paneIds, no reuse).
    // Within the same session, we keep Pane instances around so scrollback
    // and cursor position survive a split / close re-render.
    if (this.currentSessionId !== activeSession.id) {
      this.disposeAllPanes();
      this.currentSessionId = activeSession.id;
    } else {
      // Drop panes that are no longer in the tree (closed since last render).
      const keep = new Set<string>();
      this.collectLeafIds(activeSession.rootPane, keep);
      for (const id of [...this.panes.keys()]) {
        if (!keep.has(id)) {
          this.panes.get(id)?.dispose();
          this.panes.delete(id);
        }
      }
    }

    this.root.replaceChildren();
    this.renderTree(activeSession.rootPane, this.root);

    // Apply active marker after the DOM is up.
    this.setActive(activePaneId);
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

  /** Update the active-pane visual marker. */
  setActive(paneId: string | null) {
    if (this.activePaneId && this.activePaneId !== paneId) {
      this.panes.get(this.activePaneId)?.setActive(false);
    }
    this.activePaneId = paneId;
    if (paneId) {
      const pane = this.panes.get(paneId);
      pane?.setActive(true);
      // Forward keyboard focus too so typing always lands in the active pane
      // -- requestAnimationFrame ensures the DOM is settled before focus.
      requestAnimationFrame(() => pane?.focus());
    }
  }

  getActivePaneId(): string | null { return this.activePaneId; }

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
        pane = new Pane(node.paneId, node.name);
        this.panes.set(node.paneId, pane);
        pane.attach(host);
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
