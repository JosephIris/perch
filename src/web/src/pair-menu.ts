// Right-click menu on a sidebar session row: pair this tab with another for
// cross-session messaging, or dissolve the pair it has. Modeled on
// model-menu.ts (same fixed-position popup, same dismiss rules) but built
// per-invocation from the current session list.
//
// Deliberately small: pairing is the only row action that needs a menu today.
// If a second action ever lands here, promote this to a generic row menu
// rather than growing a parallel one.

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
    const label = document.createElement("div");
    if (candidates.length) {
      label.className = "pair-menu__section";
      label.textContent = "Pair with";
      menu.insertBefore(label, menu.firstChild);
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
