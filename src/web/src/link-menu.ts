// Floating action menu for terminal URL clicks. Replaces xterm's default
// "open in window.open" with our own four-action menu that wires to the
// host (default browser, in-pane right/down) plus a copy.

import { send } from "./bridge.js";

type Action = "browser" | "pane-right" | "pane-down" | "copy";

interface MenuOption {
  action: Action;
  label: string;
  hint?: string;
  icon: string;        // inline SVG path data
}

const OPTIONS: MenuOption[] = [
  {
    action: "browser",
    label: "Open in default browser",
    hint: "Enter",
    icon:
      // external-link icon (Lucide-style)
      "M15 3h6v6M10 14L21 3M21 14v7H3V3h7",
  },
  {
    action: "pane-right",
    label: "Open in pane right",
    hint: "Ctrl+→",
    icon:
      // arrow-right
      "M5 12h14M12 5l7 7-7 7",
  },
  {
    action: "pane-down",
    label: "Open in pane below",
    hint: "Ctrl+↓",
    icon: "M12 5v14M5 12l7 7 7-7",
  },
  {
    action: "copy",
    label: "Copy URL",
    hint: "Ctrl+C",
    icon:
      // copy icon
      "M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2v-2 M16 4a2 2 0 0 0-2-2H10a2 2 0 0 0-2 2 M16 4a2 2 0 0 1 2 2v0a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2v0a2 2 0 0 1 2-2",
  },
];

let openMenu: HTMLElement | null = null;

/** Show the link-action menu near (x, y) for the given URL. Returns a
 *  cleanup function the caller can ignore — the menu also self-dismisses
 *  on Esc / click-outside. */
export function showLinkMenu(url: string, x: number, y: number, paneId: string | null): void {
  dismissLinkMenu();

  const menu = document.createElement("div");
  menu.className = "link-menu";
  menu.setAttribute("role", "menu");
  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;

  const urlRow = document.createElement("div");
  urlRow.className = "link-menu__url";
  urlRow.textContent = url;
  urlRow.title = url;
  menu.appendChild(urlRow);

  for (const opt of OPTIONS) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "link-menu__item";
    btn.setAttribute("role", "menuitem");

    const iconEl = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    iconEl.setAttribute("class", "link-menu__icon");
    iconEl.setAttribute("width", "14");
    iconEl.setAttribute("height", "14");
    iconEl.setAttribute("viewBox", "0 0 24 24");
    iconEl.setAttribute("fill", "none");
    iconEl.setAttribute("stroke", "currentColor");
    iconEl.setAttribute("stroke-width", "1.7");
    iconEl.setAttribute("stroke-linecap", "round");
    iconEl.setAttribute("stroke-linejoin", "round");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", opt.icon);
    iconEl.appendChild(path);
    btn.appendChild(iconEl);

    const labelEl = document.createElement("span");
    labelEl.className = "link-menu__label";
    labelEl.textContent = opt.label;
    btn.appendChild(labelEl);

    if (opt.hint) {
      const hint = document.createElement("span");
      hint.className = "link-menu__hint";
      hint.textContent = opt.hint;
      btn.appendChild(hint);
    }

    btn.addEventListener("click", (ev) => {
      ev.stopPropagation();
      dismissLinkMenu();
      runAction(opt.action, url, paneId);
    });
    menu.appendChild(btn);
  }

  document.body.appendChild(menu);
  openMenu = menu;

  // Position adjustment: if the menu would overflow off the right or bottom
  // edge, flip it. Measure after attach so we know the real size.
  const rect = menu.getBoundingClientRect();
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  if (rect.right > vw - 8) menu.style.left = `${Math.max(8, vw - rect.width - 8)}px`;
  if (rect.bottom > vh - 8) menu.style.top = `${Math.max(8, vh - rect.height - 8)}px`;

  // Focus first item so keyboard nav works immediately.
  const firstBtn = menu.querySelector<HTMLButtonElement>(".link-menu__item");
  firstBtn?.focus();

  setTimeout(() => {
    document.addEventListener("mousedown", outsideMouseDown, true);
    document.addEventListener("keydown", onKeyDown, true);
  }, 0);
}

export function dismissLinkMenu(): void {
  if (!openMenu) return;
  openMenu.remove();
  openMenu = null;
  document.removeEventListener("mousedown", outsideMouseDown, true);
  document.removeEventListener("keydown", onKeyDown, true);
}

function outsideMouseDown(ev: MouseEvent) {
  if (openMenu && !openMenu.contains(ev.target as Node)) {
    dismissLinkMenu();
  }
}

function onKeyDown(ev: KeyboardEvent) {
  if (!openMenu) return;
  if (ev.key === "Escape") {
    ev.preventDefault();
    dismissLinkMenu();
    return;
  }
  // Arrow keys for menu nav.
  const items = openMenu.querySelectorAll<HTMLButtonElement>(".link-menu__item");
  if (items.length === 0) return;
  const focused = document.activeElement as HTMLElement | null;
  const idx = Array.from(items).indexOf(focused as HTMLButtonElement);
  if (ev.key === "ArrowDown") {
    ev.preventDefault();
    items[(idx + 1 + items.length) % items.length].focus();
  } else if (ev.key === "ArrowUp") {
    ev.preventDefault();
    items[(idx - 1 + items.length) % items.length].focus();
  } else if (ev.key === "Enter" && focused?.classList.contains("link-menu__item")) {
    // Click handler will dispatch via the buttons' own click listeners.
    ev.preventDefault();
    (focused as HTMLButtonElement).click();
  }
}

function runAction(action: Action, url: string, paneId: string | null) {
  switch (action) {
    case "browser":
      send({ type: "url.open", url });
      break;
    case "pane-right":
      if (paneId) send({ type: "pane.split", paneId, dir: "right", url });
      break;
    case "pane-down":
      if (paneId) send({ type: "pane.split", paneId, dir: "down", url });
      break;
    case "copy":
      navigator.clipboard.writeText(url).catch((err) => {
        console.error("[link-menu] clipboard write failed:", err);
      });
      break;
  }
}
