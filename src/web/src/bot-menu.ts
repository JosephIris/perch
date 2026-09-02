// Right-click menu on a bot — its roster row in the team room or its tab row
// in the sidebar. Same fixed-position popup and dismiss rules as
// project-menu.ts (they share the CSS group), its own module because the
// target is a bot, not a project or a plain session.

import type { ProjectView, TeamBotView } from "./bridge.js";
import { send } from "./bridge.js";
import { confirmWithOption } from "./confirm.js";
import { showBriefEditor } from "./new-bot-dialog.js";

let openMenu: HTMLElement | null = null;

/** Show the bot menu at a fixed viewport position. `onOpenTerminal` lets the
 *  room close itself before the tab switch; the sidebar passes nothing. */
export function showBotMenu(
  x: number,
  y: number,
  project: ProjectView,
  bot: TeamBotView,
  onOpenTerminal?: () => void,
): void {
  dismissBotMenu();

  const menu = document.createElement("div");
  menu.className = "project-menu bot-menu";
  menu.setAttribute("role", "menu");

  const item = (label: string, hint: string | null, onClick: () => void, danger = false) => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "project-menu__item" + (danger ? " project-menu__item--danger" : "");
    btn.setAttribute("role", "menuitem");
    const l = document.createElement("span");
    l.className = "project-menu__label";
    l.textContent = label;
    btn.appendChild(l);
    if (hint) {
      const h = document.createElement("span");
      h.className = "project-menu__hint";
      h.textContent = hint;
      btn.appendChild(h);
    }
    btn.addEventListener("click", (ev) => {
      ev.stopPropagation();
      dismissBotMenu();
      onClick();
    });
    menu.appendChild(btn);
    return btn;
  };

  const first = item(
    "Open terminal",
    bot.sessionId ? null : "not running",
    () => {
      if (!bot.sessionId) return;
      onOpenTerminal?.();
      send({ type: "session.select", id: bot.sessionId });
    },
  );
  if (!bot.sessionId) first.disabled = true;

  item("Edit brief…", bot.positionName, () => {
    const position = project.team?.positions.find((p) => p.slug === bot.positionSlug);
    if (position) showBriefEditor(project, position);
  });

  item("Remove from team…", null, () => {
    void confirmWithOption({
      title: `Remove ${bot.nickname} from the team?`,
      body: `${bot.nickname} stops being ${articleFor(bot.positionName)} ${bot.positionName} and leaves the room. The position and its brief stay for other bots.`,
      confirmLabel: "Remove",
      danger: true,
      option: {
        label: "Also close its tab",
        hint: "The worktree and branch stay on disk.",
      },
    }).then((r) => {
      if (!r.ok) return;
      send({ type: "team.bot.remove", projectId: project.id, botId: bot.botId, closeTab: r.optionChecked });
    });
  }, true);

  document.body.appendChild(menu);
  openMenu = menu;

  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;
  const pr = menu.getBoundingClientRect();
  if (pr.right > window.innerWidth - 8)
    menu.style.left = `${Math.max(8, window.innerWidth - pr.width - 8)}px`;
  if (pr.bottom > window.innerHeight - 8)
    menu.style.top = `${Math.max(8, y - pr.height)}px`;

  (menu.querySelector<HTMLButtonElement>("button:not(:disabled)"))?.focus();

  setTimeout(() => {
    document.addEventListener("mousedown", outsideMouseDown, true);
    document.addEventListener("keydown", onKeyDown, true);
  }, 0);
}

export function dismissBotMenu(): void {
  if (!openMenu) return;
  openMenu.remove();
  openMenu = null;
  document.removeEventListener("mousedown", outsideMouseDown, true);
  document.removeEventListener("keydown", onKeyDown, true);
}

/** "a Frontend dev" / "an Analyst" — the article a position name takes. */
export function articleFor(name: string): string {
  return /^[aeiou]/i.test(name.trim()) ? "an" : "a";
}

function outsideMouseDown(ev: MouseEvent): void {
  if (openMenu && !openMenu.contains(ev.target as Node)) dismissBotMenu();
}

function onKeyDown(ev: KeyboardEvent): void {
  if (!openMenu) return;
  if (ev.key === "Escape") {
    ev.preventDefault();
    ev.stopPropagation();
    dismissBotMenu();
    return;
  }
  const items = Array.from(openMenu.querySelectorAll<HTMLButtonElement>(".project-menu__item:not(:disabled)"));
  if (items.length === 0) return;
  const idx = items.indexOf(document.activeElement as HTMLButtonElement);
  if (ev.key === "ArrowDown") { ev.preventDefault(); items[(idx + 1 + items.length) % items.length].focus(); }
  else if (ev.key === "ArrowUp") { ev.preventDefault(); items[(idx - 1 + items.length) % items.length].focus(); }
}
