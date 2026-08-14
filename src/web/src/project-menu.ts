// Right-click menu on a sidebar project header: hide the project (fold it
// into project mode's "Hidden" drawer) or show it again. Same fixed-position
// popup and dismiss rules as pair-menu.ts, sharing its CSS (style.css groups
// the selectors) — but its own module, because the target is a project
// header, not a session row, and the two menus will grow different actions.

import type { ProjectView } from "./bridge.js";
import { send } from "./bridge.js";

let openMenu: HTMLElement | null = null;

/** Show the project menu for `p` at a fixed viewport position (the
 *  right-click point). */
export function showProjectMenu(x: number, y: number, p: ProjectView): void {
  dismissProjectMenu();

  const menu = document.createElement("div");
  menu.className = "project-menu";
  menu.setAttribute("role", "menu");

  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "project-menu__item";
  btn.setAttribute("role", "menuitem");
  const label = document.createElement("span");
  label.className = "project-menu__label";
  label.textContent = p.hidden ? "Show project" : "Hide project";
  btn.appendChild(label);
  if (!p.hidden) {
    // Hiding is the one direction that needs reassurance: say where it goes.
    const hint = document.createElement("span");
    hint.className = "project-menu__hint";
    hint.textContent = "moves to Hidden, below";
    btn.appendChild(hint);
  }
  btn.addEventListener("click", (ev) => {
    ev.stopPropagation();
    dismissProjectMenu();
    send({ type: "project.update", id: p.id, hidden: !p.hidden });
  });
  menu.appendChild(btn);

  document.body.appendChild(menu);
  openMenu = menu;

  // Clamp to the viewport (same flip rules as the pair menu).
  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;
  const pr = menu.getBoundingClientRect();
  if (pr.right > window.innerWidth - 8)
    menu.style.left = `${Math.max(8, window.innerWidth - pr.width - 8)}px`;
  if (pr.bottom > window.innerHeight - 8)
    menu.style.top = `${Math.max(8, y - pr.height)}px`;

  btn.focus();

  setTimeout(() => {
    document.addEventListener("mousedown", outsideMouseDown, true);
    document.addEventListener("keydown", onKeyDown, true);
  }, 0);
}

export function dismissProjectMenu(): void {
  if (!openMenu) return;
  openMenu.remove();
  openMenu = null;
  document.removeEventListener("mousedown", outsideMouseDown, true);
  document.removeEventListener("keydown", onKeyDown, true);
}

function outsideMouseDown(ev: MouseEvent): void {
  if (openMenu && !openMenu.contains(ev.target as Node)) dismissProjectMenu();
}

function onKeyDown(ev: KeyboardEvent): void {
  if (!openMenu) return;
  if (ev.key === "Escape") {
    ev.preventDefault();
    ev.stopPropagation();
    dismissProjectMenu();
  }
}
