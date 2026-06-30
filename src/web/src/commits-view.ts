// The "ready to push" recap UI. Three presentations of the same unpushed-commit
// data (fetched via commits.ts):
//
//   • openCommitsPopover(anchor, paneId)  — sticky, interactive flyout off the
//     footer "↑N" chip. Compact list + a "View details" link into the lightbox.
//   • attachCommitsHover(anchor, paneId)  — read-only tooltip on a sidebar row's
//     ahead count. Same compact list; non-interactive (pointer-events: none).
//   • openCommitsLightbox(paneId)          — full modal: every commit with its
//     per-file diff. Opened by a sidebar-row / dashboard-card click.
//
// Commits are grouped "this session" (made since the agent baseline) vs
// "earlier unpushed"; the group labels only appear when both groups exist.

import { requestCommits } from "./commits.js";
import { fmtAgo } from "./elapsed.js";
import type { CommitsDataMessage, CommitView } from "./bridge.js";

const HOVER_DELAY_MS = 450;

const el = (tag: string, cls?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  return e;
};
const elText = (tag: string, cls: string, text: string): HTMLElement => {
  const e = el(tag, cls);
  e.textContent = text;
  return e;
};

// ---- Shared rendering ------------------------------------------------------

function headerEl(ahead: number): HTMLElement {
  const h = el("div", "commits-pop__head");
  h.appendChild(elText("span", "commits-pop__count", `↑${ahead}`));
  h.append(" ");
  h.appendChild(elText("span", "commits-pop__head-label", "ready to push"));
  return h;
}

function loadingEl(): HTMLElement {
  return elText("div", "commits-pop__loading", "Loading…");
}

/** Append a "+A −B" cluster (reusing the app-wide diff palette) to `host`. */
function appendDiff(host: HTMLElement, added: number, deleted: number): void {
  if (added) host.appendChild(elText("span", "diff-add", `+${added}`));
  if (deleted) {
    if (added) host.append(" ");
    host.appendChild(elText("span", "diff-del", `−${deleted}`));
  }
}

function commitRow(c: CommitView, full: boolean): HTMLElement {
  const row = el("div", "commit-row");
  row.appendChild(elText("div", "commit-row__subject", c.subject || "(no message)"));

  const meta = el("div", "commit-row__meta");
  meta.appendChild(elText("span", "commit-row__sha", c.sha));
  const ts = Date.parse(c.committedIso);
  if (!Number.isNaN(ts)) {
    meta.append(" · ");
    meta.appendChild(elText("span", "commit-row__ago", fmtAgo(Date.now() - ts)));
  }
  if (c.added || c.deleted) {
    meta.append(" · ");
    appendDiff(meta, c.added, c.deleted);
  }
  row.appendChild(meta);

  if (full && c.files.length) {
    const files = el("div", "commit-row__files");
    for (const f of c.files) {
      const fr = el("div", "commit-file");
      const counts = el("span", "commit-file__counts");
      if (f.added || f.deleted) appendDiff(counts, f.added, f.deleted);
      else counts.appendChild(elText("span", "diff-files", "bin"));
      fr.appendChild(counts);
      fr.appendChild(elText("span", "commit-file__path", f.path));
      files.appendChild(fr);
    }
    row.appendChild(files);
  }
  return row;
}

/** Build the grouped commit list (or an empty-state line). */
function listEl(data: CommitsDataMessage, full: boolean): HTMLElement {
  const wrap = el("div", "commits-list");
  if (data.commits.length === 0) {
    wrap.appendChild(elText("div", "commits-list__empty", "Nothing to push — up to date."));
    return wrap;
  }
  const session = data.commits.filter((c) => c.inSession);
  const earlier = data.commits.filter((c) => !c.inSession);
  const bothGroups = session.length > 0 && earlier.length > 0;
  const addGroup = (label: string, list: CommitView[]) => {
    if (!list.length) return;
    if (bothGroups) wrap.appendChild(elText("div", "commits-list__group", label));
    for (const c of list) wrap.appendChild(commitRow(c, full));
  };
  addGroup("this session", session);
  addGroup("earlier unpushed", earlier);
  return wrap;
}

/** Anchor a fixed-position panel below `anchor`, flipping above / clamping
 *  horizontally so it never spills off-screen. Mirrors the color-picker. */
function placeNear(panel: HTMLElement, anchor: HTMLElement): void {
  const a = anchor.getBoundingClientRect();
  const p = panel.getBoundingClientRect();
  const margin = 8;
  let left = a.left;
  if (left + p.width > window.innerWidth - margin) {
    left = Math.max(margin, window.innerWidth - p.width - margin);
  }
  let top = a.bottom + 6;
  if (top + p.height > window.innerHeight - margin) {
    top = Math.max(margin, a.top - p.height - 6);
  }
  panel.style.left = `${left}px`;
  panel.style.top = `${top}px`;
}

// ---- Popover (footer chip click) -------------------------------------------

let popover: HTMLElement | null = null;
let popToken = 0;

export function openCommitsPopover(anchor: HTMLElement, paneId: string): void {
  dismissPopover();
  const token = ++popToken;

  const pop = el("div", "commits-pop");
  pop.appendChild(loadingEl());
  document.body.appendChild(pop);
  popover = pop;
  placeNear(pop, anchor);

  requestCommits(paneId).then((data) => {
    if (popover !== pop || popToken !== token) return;
    pop.replaceChildren(headerEl(data.ahead));
    const body = el("div", "commits-pop__body");
    body.appendChild(listEl(data, /* full */ false));
    pop.appendChild(body);
    if (data.commits.length) {
      const more = document.createElement("button");
      more.className = "commits-pop__more";
      more.type = "button";
      more.textContent = "View details";
      more.addEventListener("click", (ev) => {
        ev.stopPropagation();
        dismissPopover();
        openCommitsLightbox(paneId);
      });
      pop.appendChild(more);
    }
    placeNear(pop, anchor);
  });

  // Defer so the click that opened us doesn't immediately dismiss it.
  setTimeout(() => {
    document.addEventListener("mousedown", onPopOutside, true);
    document.addEventListener("keydown", onPopKey, true);
  }, 0);
}

function dismissPopover(): void {
  if (!popover) return;
  popover.remove();
  popover = null;
  document.removeEventListener("mousedown", onPopOutside, true);
  document.removeEventListener("keydown", onPopKey, true);
}
function onPopOutside(ev: MouseEvent): void {
  if (popover && !popover.contains(ev.target as Node)) dismissPopover();
}
function onPopKey(ev: KeyboardEvent): void {
  if (ev.key === "Escape") {
    ev.preventDefault();
    ev.stopPropagation();
    dismissPopover();
  }
}

// ---- Hover tooltip (sidebar ahead count) -----------------------------------

let hoverPanel: HTMLElement | null = null;
let hoverTimer = 0;
let hoverAnchor: HTMLElement | null = null;
let hoverToken = 0;

function ensureHoverPanel(): HTMLElement {
  if (hoverPanel) return hoverPanel;
  const p = el("div", "commits-pop commits-pop--hover");
  p.setAttribute("role", "tooltip");
  document.body.appendChild(p);
  hoverPanel = p;
  return p;
}

function hideHover(): void {
  if (hoverTimer) {
    clearTimeout(hoverTimer);
    hoverTimer = 0;
  }
  hoverAnchor = null;
  hoverToken++;
  if (hoverPanel) hoverPanel.classList.remove("commits-pop--visible");
}

function showHover(anchor: HTMLElement, paneId: string, token: number): void {
  const panel = ensureHoverPanel();
  panel.replaceChildren(loadingEl());
  panel.classList.add("commits-pop--visible");
  placeNear(panel, anchor);
  requestCommits(paneId).then((data) => {
    if (hoverToken !== token || hoverAnchor !== anchor) return;
    panel.replaceChildren(headerEl(data.ahead));
    const body = el("div", "commits-pop__body");
    body.appendChild(listEl(data, /* full */ false));
    panel.appendChild(body);
    placeNear(panel, anchor);
  });
}

/** Show the compact recap as a hover tooltip on `anchor`. Re-call on each
 *  sidebar rebuild with the row's current paneId (old DOM is discarded). */
export function attachCommitsHover(anchor: HTMLElement, paneId: string): void {
  anchor.addEventListener("mouseenter", () => {
    hoverAnchor = anchor;
    const token = ++hoverToken;
    if (hoverTimer) clearTimeout(hoverTimer);
    hoverTimer = window.setTimeout(() => {
      hoverTimer = 0;
      if (hoverAnchor === anchor) showHover(anchor, paneId, token);
    }, HOVER_DELAY_MS);
  });
  anchor.addEventListener("mouseleave", hideHover);
  anchor.addEventListener("mousedown", hideHover);
}

// ---- Lightbox (full detail) ------------------------------------------------

let lightboxOpen = false;

export function openCommitsLightbox(paneId: string): void {
  if (lightboxOpen) return;
  lightboxOpen = true;

  const overlay = el("div", "settings-overlay");
  const card = el("div", "settings-card commits-lightbox");
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");

  const title = elText("h2", "settings-card__title", "Ready to push");
  card.appendChild(title);

  const body = el("div", "commits-lightbox__body");
  body.appendChild(loadingEl());
  card.appendChild(body);

  overlay.appendChild(card);
  document.body.appendChild(overlay);

  let settled = false;
  const finish = () => {
    if (settled) return;
    settled = true;
    lightboxOpen = false;
    window.removeEventListener("keydown", onKey, true);
    overlay.classList.add("settings-overlay--closing");
    overlay.addEventListener("animationend", () => overlay.remove(), { once: true });
    window.setTimeout(() => overlay.remove(), 260); // reduced-motion fallback
  };
  function onKey(ev: KeyboardEvent) {
    if (ev.key === "Escape") {
      ev.preventDefault();
      ev.stopPropagation();
      finish();
    }
  }
  overlay.addEventListener("mousedown", (ev) => {
    if (ev.target === overlay) finish();
  });
  // On window/capture so it still fires when an open dashboard's Esc handler
  // would otherwise swallow the key (same target+phase → stopPropagation there
  // doesn't block this sibling listener).
  window.addEventListener("keydown", onKey, true);

  requestCommits(paneId).then((data) => {
    if (settled) return;
    title.textContent = data.ahead > 0 ? `${data.ahead} ready to push` : "Ready to push";
    body.replaceChildren(listEl(data, /* full */ true));
  });
}
