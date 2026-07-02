// Pure layout math shared by workspace.ts — no DOM, no xterm, so it can be
// unit-tested directly (test/layout.test.ts). Two things live here:
//
//  • treeSignature — the rebuild gate. A signature mismatch is what makes the
//    workspace tear down + remount pane DOM, and a remount costs every xterm
//    its in-flight CSS transitions (and, if it fires when it shouldn't, reads
//    as the "scrollback dies" class of bug). Weight is deliberately NOT part
//    of the signature: a gutter drag must never force a rebuild.
//
//  • computeEdge — which drop zone a normalized pointer position lands in
//    during drag-to-rearrange: a centered box is "center" (swap), otherwise
//    the nearest edge (re-split).

import type { PaneTreeView } from "./bridge.js";

/** Drop zone within a target pane during a drag-to-rearrange. */
export type Edge = "left" | "right" | "top" | "bottom" | "center";

/** Stable string for a pane tree's SHAPE — leaf paneIds + split
 *  orientations, in document order. Used to skip the DOM rebuild on
 *  no-op renders (pure focus changes). Does NOT include the active-pane
 *  marker or any other state that toggles via setActive. */
export function treeSignature(node: PaneTreeView): string {
  if (node.kind === "leaf") return `L:${node.paneId}:${node.url ?? ""}`;
  return `S(${node.orientation}:${node.children.map(treeSignature).join(",")})`;
}

/** Which drop zone the pointer is in, given its position normalized to the
 *  target pane's box (0..1 on each axis): a centered box is "center" (swap),
 *  otherwise the nearest edge. */
export function computeEdge(x: number, y: number): Edge {
  if (Math.abs(x - 0.5) < 0.22 && Math.abs(y - 0.5) < 0.22) return "center";
  const d = { left: x, right: 1 - x, top: y, bottom: 1 - y };
  let edge: Edge = "left";
  let min = d.left;
  for (const k of ["right", "top", "bottom"] as const) {
    if (d[k] < min) { min = d[k]; edge = k; }
  }
  return edge;
}
