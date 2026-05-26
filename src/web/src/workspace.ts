// The workspace is everything between the sidebar and the status bar -- the
// active session's pane tree. Stage 3a: every session has a single leaf, so
// the workspace just shows one Pane. Stage 3b promotes this into a recursive
// CSS-Grid layout that mirrors the PaneTreeView union from bridge.ts.

import type { PaneTreeView, SessionView } from "./bridge.js";
import { Pane } from "./pane.js";

export class Workspace {
  private readonly root: HTMLElement;
  private readonly panes = new Map<string, Pane>();
  // Bytes that arrive before the matching Pane is attached. The host can
  // start streaming PTY output the moment ConPty.Start returns, and that
  // race can beat the page's state-then-attach sequence by tens of ms.
  // Replay on attach so no banner gets dropped.
  private readonly pendingBytes = new Map<string, string[]>();
  private currentSessionId: string | null = null;

  constructor(rootEl: HTMLElement) {
    this.root = rootEl;
  }

  /** Reconcile the workspace to the active session's pane tree. Stage 3a
   *  only ever has a single leaf per session, so this is currently a
   *  swap-or-leave operation. */
  render(activeSession: SessionView | null) {
    if (!activeSession) {
      this.clear();
      return;
    }

    if (this.currentSessionId !== activeSession.id) {
      // Stash existing panes by paneId so the new layout can pick them
      // back up when we re-render the same session later (stage 3b makes
      // that meaningful when split layouts persist).
      this.clearDom();
      this.currentSessionId = activeSession.id;
    }

    this.renderTree(activeSession.rootPane, this.root);
  }

  /** Receive bytes from the host. If the Pane hasn't been attached yet
   *  (state reconciliation still in flight), park the bytes; they'll
   *  replay when renderTree creates the Pane. */
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

  focus(paneId: string) {
    this.panes.get(paneId)?.focus();
  }

  /** Drop every Pane (and dispose their xterm instances) and empty the
   *  workspace DOM. Used on session change. */
  private clear() {
    for (const p of this.panes.values()) p.dispose();
    this.panes.clear();
    this.root.replaceChildren();
    this.currentSessionId = null;
  }

  private clearDom() {
    for (const p of this.panes.values()) p.dispose();
    this.panes.clear();
    this.root.replaceChildren();
  }

  private renderTree(node: PaneTreeView, host: HTMLElement) {
    if (node.kind === "leaf") {
      const pane = new Pane(node.paneId);
      this.panes.set(node.paneId, pane);
      pane.attach(host);
      // Replay any bytes that arrived before this Pane existed.
      const queued = this.pendingBytes.get(node.paneId);
      if (queued) {
        for (const b64 of queued) pane.feed(b64);
        this.pendingBytes.delete(node.paneId);
      }
      // Auto-focus the first pane so keyboard input flows immediately.
      if (this.panes.size === 1) requestAnimationFrame(() => pane.focus());
      return;
    }
    // Stage 3b implements splits here. Leaving the structure in place so
    // the reconciliation contract is unchanged when we get to it.
    const splitEl = document.createElement("div");
    splitEl.className = `split split--${node.orientation}`;
    host.appendChild(splitEl);
    for (const child of node.children) this.renderTree(child, splitEl);
  }
}
