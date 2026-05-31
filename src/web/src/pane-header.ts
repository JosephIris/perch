// Shared header builder for both terminal panes (Pane) and webview panes
// (UrlPane). The visual contract is the same:
//
//   [● color]  pane-name  [● state · state-word]   [✕ close]
//
//   - color dot: click to cycle / pick (calls pane.recolor)
//   - name: double-click to rename (calls pane.rename); Enter commits,
//     Escape cancels, blur commits.
//   - state dot + word: read-only, driven by Pane.applyLeafView
//   - close: sends pane.close
//
// Returning the element handles back so Pane / UrlPane can stash them
// and update on each applyLeafView.

import { send } from "./bridge.js";
import type { PaneTreeView } from "./bridge.js";

export interface PaneHeader {
  root: HTMLElement;
  colorDotEl: HTMLButtonElement;
  nameEl: HTMLElement;
  stateDotEl: HTMLElement;
  stateLabelEl: HTMLElement;
  /** Chip for commits-since-session-start; hidden when count is 0. */
  commitsEl: HTMLElement;
  /** Chip for the auto-detected branch; hidden when empty. */
  branchEl: HTMLElement;
}

const COLOR_COUNT = 6;

/** Render branch + commit-count chips in the pane header. Both elements
 *  are toggled visible only when they have content; styling lives in
 *  style.css (.pane__chip + variants).
 *
 *  The commit chip is shown only on the ACTIVE pane. Commits are repo-wide,
 *  not pane-scoped — panes sharing a worktree all see the same HEAD, so
 *  surfacing "+N commits" on every one of them was noise. Gating on focus
 *  pins it to the single pane the user is looking at. */
export function applyChips(
  branchEl: HTMLElement,
  commitsEl: HTMLElement,
  leaf: Extract<PaneTreeView, { kind: "leaf" }>,
  active: boolean
) {
  if (leaf.branch) {
    branchEl.textContent = `⎇ ${leaf.branch}`;
    branchEl.style.display = "";
  } else {
    branchEl.textContent = "";
    branchEl.style.display = "none";
  }
  if (leaf.commitCount > 0 && active) {
    commitsEl.textContent = `+${leaf.commitCount} commit${leaf.commitCount === 1 ? "" : "s"}`;
    commitsEl.style.display = "";
  } else {
    commitsEl.textContent = "";
    commitsEl.style.display = "none";
  }
}

export function buildPaneHeader(paneId: string): PaneHeader {
  const root = document.createElement("div");
  root.className = "pane__header";

  const colorDotEl = document.createElement("button");
  colorDotEl.type = "button";
  colorDotEl.className = "pane__color";
  colorDotEl.title = "Change color tag";
  colorDotEl.setAttribute("aria-label", "Change color tag");
  colorDotEl.dataset.color = "0";
  // Click opens a small 6-swatch picker anchored under the dot. Picking
  // a swatch fires pane.recolor + closes; Esc / click-outside dismiss.
  colorDotEl.addEventListener("click", (ev) => {
    ev.stopPropagation();
    const current = parseInt(colorDotEl.dataset.color ?? "0", 10);
    openColorPicker(colorDotEl, current, (idx) => {
      send({ type: "pane.recolor", paneId, colorIndex: idx });
    });
  });
  root.appendChild(colorDotEl);

  const nameEl = document.createElement("span");
  nameEl.className = "pane__name";
  // No native `title` — Pane attaches a custom tooltip showing the full
  // first-prompt text (falling back to a rename hint) via attachTooltip.
  nameEl.addEventListener("dblclick", (ev) => {
    ev.stopPropagation();
    startInlineRename(paneId, nameEl);
  });
  root.appendChild(nameEl);

  // Branch chip (auto-detected from cwd via OSC 7 → git rev-parse) and
  // commit-count chip (cc-session baseline → git rev-list). Both empty by
  // default; applyLeafView toggles their visibility per pane state.
  const branchEl = document.createElement("span");
  branchEl.className = "pane__chip pane__chip--branch";
  root.appendChild(branchEl);

  const commitsEl = document.createElement("span");
  commitsEl.className = "pane__chip pane__chip--commits";
  root.appendChild(commitsEl);

  // State indicator (dot + word). Hidden when state is "idle" via the
  // empty label + CSS — dot stays so the visual rhythm doesn't jump.
  const stateDotEl = document.createElement("span");
  stateDotEl.className = "pane__state-dot";
  stateDotEl.dataset.state = "idle";
  root.appendChild(stateDotEl);

  const stateLabelEl = document.createElement("span");
  stateLabelEl.className = "pane__state-label";
  root.appendChild(stateLabelEl);

  const close = document.createElement("button");
  close.type = "button";
  close.className = "pane__close";
  close.title = "Close pane";
  close.setAttribute("aria-label", "Close pane");
  close.textContent = "✕";
  close.addEventListener("click", (ev) => {
    ev.stopPropagation();
    send({ type: "pane.close", paneId });
  });
  root.appendChild(close);

  return { root, colorDotEl, nameEl, stateDotEl, stateLabelEl, branchEl, commitsEl };
}

let openPicker: HTMLElement | null = null;

function openColorPicker(
  anchor: HTMLElement,
  current: number,
  onPick: (idx: number) => void
) {
  dismissColorPicker();

  const picker = document.createElement("div");
  picker.className = "color-picker";
  picker.setAttribute("role", "menu");

  for (let i = 0; i < COLOR_COUNT; i++) {
    const sw = document.createElement("button");
    sw.type = "button";
    sw.className = "color-picker__swatch" + (i === current ? " color-picker__swatch--current" : "");
    sw.dataset.color = String(i);
    sw.setAttribute("aria-label", `Color ${i + 1}`);
    sw.addEventListener("click", (ev) => {
      ev.stopPropagation();
      dismissColorPicker();
      onPick(i);
    });
    picker.appendChild(sw);
  }

  document.body.appendChild(picker);
  openPicker = picker;

  // Anchor below the dot, aligned left. Flip to fit if it would clip.
  const rect = anchor.getBoundingClientRect();
  picker.style.left = `${rect.left}px`;
  picker.style.top = `${rect.bottom + 6}px`;
  const pr = picker.getBoundingClientRect();
  if (pr.right > window.innerWidth - 8) {
    picker.style.left = `${Math.max(8, window.innerWidth - pr.width - 8)}px`;
  }

  setTimeout(() => {
    document.addEventListener("mousedown", colorPickerOutside, true);
    document.addEventListener("keydown", colorPickerKey, true);
  }, 0);
}

function dismissColorPicker() {
  if (!openPicker) return;
  openPicker.remove();
  openPicker = null;
  document.removeEventListener("mousedown", colorPickerOutside, true);
  document.removeEventListener("keydown", colorPickerKey, true);
}
function colorPickerOutside(ev: MouseEvent) {
  if (openPicker && !openPicker.contains(ev.target as Node)) {
    dismissColorPicker();
  }
}
function colorPickerKey(ev: KeyboardEvent) {
  if (ev.key === "Escape") {
    ev.preventDefault();
    dismissColorPicker();
  }
}

function startInlineRename(paneId: string, nameEl: HTMLElement) {
  const original = nameEl.textContent ?? "";
  const input = document.createElement("input");
  input.type = "text";
  input.className = "pane__name-input";
  input.value = original;
  input.spellcheck = false;

  let committed = false;
  function commit() {
    if (committed) return;
    committed = true;
    const next = input.value.trim();
    input.replaceWith(nameEl);
    if (next && next !== original) {
      // Optimistic: update local DOM immediately. The host will echo
      // back the new name in the next state push.
      nameEl.textContent = next;
      send({ type: "pane.rename", paneId, name: next });
    } else {
      nameEl.textContent = original;
    }
  }
  function cancel() {
    if (committed) return;
    committed = true;
    input.replaceWith(nameEl);
    nameEl.textContent = original;
  }

  input.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter") {
      ev.preventDefault();
      commit();
    } else if (ev.key === "Escape") {
      ev.preventDefault();
      cancel();
    }
    // Stop the global keybindings (Ctrl+B, Ctrl+= etc) from swallowing
    // typing in the rename input.
    ev.stopPropagation();
  });
  input.addEventListener("blur", commit);

  nameEl.replaceWith(input);
  input.focus();
  input.select();
}
