// Right-click menu on a sidebar session row: open the tab without picking up
// its saved conversation, pair it with another tab for cross-session
// messaging, or dissolve the pair it has. Modeled on model-menu.ts (same
// fixed-position popup, same dismiss rules) but built per-invocation from the
// current session list.
//
// It is a row menu now, not a pair menu — the name stayed because renaming a
// file every time it grows an item is churn, not clarity.

import type { SessionView } from "./bridge.js";
import { send } from "./bridge.js";

let openMenu: HTMLElement | null = null;

/** Show the pair menu for `s` at a fixed viewport position (the right-click
 *  point). `all` is the full session list this render; candidates are every
 *  other live tab, same-project first. */
export function showPairMenu(x: number, y: number, s: SessionView, all: SessionView[]): void {
  dismissPairMenu();

  const menu = document.createElement("div");
  menu.className = "pair-menu";
  menu.setAttribute("role", "menu");

  const items: HTMLButtonElement[] = [];
  const add = (label: string, hint: string | null, onPick: () => void) => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "pair-menu__item";
    btn.setAttribute("role", "menuitem");
    const labelEl = document.createElement("span");
    labelEl.className = "pair-menu__label";
    labelEl.textContent = label;
    btn.appendChild(labelEl);
    if (hint) {
      const hintEl = document.createElement("span");
      hintEl.className = "pair-menu__hint";
      hintEl.textContent = hint;
      btn.appendChild(hintEl);
    }
    btn.addEventListener("click", (ev) => {
      ev.stopPropagation();
      dismissPairMenu();
      onPick();
    });
    menu.appendChild(btn);
    items.push(btn);
    return btn;
  };

  // The per-tab "no" to picking up a conversation. Offered only on a tab that
  // WOULD pick one up, so the menu never carries an item that does nothing.
  // This is the escape hatch that let the launch dialog go: the old global
  // "Not now" is now a per-tab choice, made at the moment it matters.
  if ((s.resumesOnOpen ?? 0) > 0) {
    add("Open as a fresh shell", "don't pick up the conversation", () =>
      send({ type: "session.openFresh", id: s.id })
    );
  }

  if (s.pairedWith) {
    const partner = all.find((t) => t.id === s.pairedWith);
    add(`Unpair from ${partner?.title ?? "its partner"}`, null, () =>
      send({ type: "session.unpair", id: s.id })
    );
  } else {
    // Same-project tabs first (the common pairing), then the rest, both in
    // sidebar order. Dormant tabs stay offered — the pairing survives sleep
    // and the introduction is delivered when the tab wakes.
    const candidates = [
      ...all.filter((t) => t.id !== s.id && t.projectId === s.projectId),
      ...all.filter((t) => t.id !== s.id && t.projectId !== s.projectId),
    ];
    if (!candidates.length) {
      const none = document.createElement("div");
      none.className = "pair-menu__empty";
      none.textContent = "No other tab to pair with";
      menu.appendChild(none);
    }
    // Appended, not inserted at the top: the menu may already carry "Open as
    // a fresh shell" above, and a section label that jumps over it would
    // caption the wrong item.
    if (candidates.length) {
      const label = document.createElement("div");
      label.className = "pair-menu__section";
      label.textContent = "Pair with";
      menu.appendChild(label);
    }
    for (const t of candidates.slice(0, 8)) {
      add(t.title, t.projectId === s.projectId ? null : "other project", () =>
        send({ type: "session.pair", id: s.id, partnerId: t.id })
      );
    }
  }

  document.body.appendChild(menu);
  openMenu = menu;

  // Clamp to the viewport (same flip rules as the model menu).
  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;
  const pr = menu.getBoundingClientRect();
  if (pr.right > window.innerWidth - 8)
    menu.style.left = `${Math.max(8, window.innerWidth - pr.width - 8)}px`;
  if (pr.bottom > window.innerHeight - 8)
    menu.style.top = `${Math.max(8, y - pr.height)}px`;

  items[0]?.focus();

  setTimeout(() => {
    document.addEventListener("mousedown", outsideMouseDown, true);
    document.addEventListener("keydown", onKeyDown, true);
  }, 0);
}

export function dismissPairMenu(): void {
  if (!openMenu) return;
  openMenu.remove();
  openMenu = null;
  document.removeEventListener("mousedown", outsideMouseDown, true);
  document.removeEventListener("keydown", onKeyDown, true);
}

function outsideMouseDown(ev: MouseEvent): void {
  if (openMenu && !openMenu.contains(ev.target as Node)) dismissPairMenu();
}

function onKeyDown(ev: KeyboardEvent): void {
  if (!openMenu) return;
  const items = [...openMenu.querySelectorAll<HTMLButtonElement>(".pair-menu__item")];
  const idx = items.findIndex((el) => el === document.activeElement);
  if (ev.key === "Escape") {
    ev.preventDefault();
    ev.stopPropagation();
    dismissPairMenu();
  } else if (ev.key === "ArrowDown") {
    ev.preventDefault();
    ev.stopPropagation();
    items[Math.min(items.length - 1, idx + 1)]?.focus();
  } else if (ev.key === "ArrowUp") {
    ev.preventDefault();
    ev.stopPropagation();
    items[Math.max(0, idx - 1)]?.focus();
  }
}
