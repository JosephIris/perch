// Per-pane status bar — the bottom counterpart to pane-header.ts. Where the
// header is identity (color · name · agent · branch · commits · state), the
// footer is *liveness*: what this pane is doing right now and the work it has
// produced. One quiet line, status-bar discipline (transparent, 1px top
// hairline, caption type, tertiary text).
//
//   [▸ editing live-updates.ts · 2m]              [+142 −38 · 3 files] [↑2] [:5173]
//   [✓ finished · 4m ago]                                              ...
//
// Left cluster is per-pane (each pane runs its own agent/turn). Right cluster's
// git stats are repo-wide — panes sharing a worktree see the same HEAD — so
// they're focus-gated to the active pane, mirroring the header's commit chip.
// Ports stay per-pane. The live "· 2m" / "· 4m ago" reuse the shared elapsed
// ticker, so they climb without the host re-pushing state.

import { elapsedSpan, agoSpan } from "./elapsed.js";
import type { PaneTreeView } from "./bridge.js";

type Leaf = Extract<PaneTreeView, { kind: "leaf" }>;

export interface PaneFooter {
  root: HTMLElement;
  /** Left: state glyph + activity detail + live elapsed/ago. */
  activityEl: HTMLElement;
  /** Right: diff / unpushed / port chips. */
  metaEl: HTMLElement;
}

export function buildPaneFooter(): PaneFooter {
  const root = document.createElement("div");
  root.className = "pane__footer";

  const activityEl = document.createElement("span");
  activityEl.className = "pane__footer-activity";
  root.appendChild(activityEl);

  // Pushes the metrics to the right edge.
  const spacer = document.createElement("span");
  spacer.className = "pane__footer-spacer";
  root.appendChild(spacer);

  const metaEl = document.createElement("span");
  metaEl.className = "pane__footer-meta";
  root.appendChild(metaEl);

  return { root, activityEl, metaEl };
}

function chip(cls: string, text: string): HTMLElement {
  const el = document.createElement("span");
  el.className = cls;
  el.textContent = text;
  return el;
}

/** Refresh the footer from the latest leaf view. Idempotent and cheap — called
 *  on every state push and whenever the pane's active flag flips. */
export function applyPaneFooter(f: PaneFooter, leaf: Leaf, active: boolean): void {
  // --- Left: live activity. Lead with the time signal the header can't show. ---
  const a = f.activityEl;
  a.dataset.state = leaf.agentState;
  a.replaceChildren();
  switch (leaf.agentState) {
    case "working":
      a.append(`▸ ${leaf.activityDetail || "working"}`);
      if (leaf.turnStartMs > 0) {
        a.append(" · ");
        // Coarse (minute) — the footer shouldn't tick a seconds counter.
        a.appendChild(elapsedSpan(leaf.turnStartMs, /* coarse */ true));
      }
      break;
    case "done":
      a.append("✓ finished");
      if (leaf.doneAtMs > 0) {
        a.append(" · ");
        a.appendChild(agoSpan(leaf.doneAtMs, /* coarse */ true));
      }
      break;
    case "waiting":
      a.append("waiting for you");
      break;
    case "permission":
      a.append("needs permission");
      break;
    // idle (dormant shell): nothing — the right cluster / collapse handles it.
  }

  // --- Right: produced work + ports. Git stats repo-wide → active pane only. ---
  const m = f.metaEl;
  m.replaceChildren();
  if (active && (leaf.linesAdded || leaf.linesDeleted)) {
    const c = chip("chip chip--diff", "");
    if (leaf.linesAdded) c.appendChild(chip("diff-add", `+${leaf.linesAdded}`));
    if (leaf.linesDeleted) c.appendChild(chip("diff-del", `−${leaf.linesDeleted}`));
    if (leaf.filesChanged)
      c.appendChild(chip("diff-files", `${leaf.filesChanged} file${leaf.filesChanged === 1 ? "" : "s"}`));
    m.appendChild(c);
  }
  if (active && leaf.ahead > 0) m.appendChild(chip("chip", `↑${leaf.ahead}`));
  for (const p of leaf.ports ?? []) m.appendChild(chip("chip", `:${p}`));

  // Collapse to nothing for a plain idle shell with no ports/diff so the bar
  // doesn't cost a terminal row when it has nothing to say.
  const empty = a.childNodes.length === 0 && m.childElementCount === 0;
  f.root.classList.toggle("pane__footer--empty", empty);
}
