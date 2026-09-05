// Per-pane Claude Code model picker. A quiet flyout anchored under the pane
// header's model label, modeled on the link-menu / color-picker idioms: a
// role=menu popover that dismisses on Esc / click-outside and sends the pick to
// the host as pane.model.
//
// Usage awareness: the host ferries the account-wide AT-LIMIT models (and their
// reset times) with every state push; setModelLimits stores the latest. A model
// that's at its limit is disabled and annotated "resets HH:MM". Usually there's
// no data at all (the usage endpoint 429s) — then every model is enabled and
// nothing is annotated.

import { send } from "./bridge.js";

export interface ModelOption {
  /** CLI alias sent to the host; "" is the account default. */
  alias: string;
  label: string;
}

// Default first, then the four aliases, sentence-cased. Exported so the
// new-tab dialog offers the same list without duplicating it.
export const MODEL_OPTIONS: ModelOption[] = [
  { alias: "", label: "Default" },
  { alias: "fable", label: "Fable" },
  { alias: "opus", label: "Opus" },
  { alias: "sonnet", label: "Sonnet" },
  { alias: "haiku", label: "Haiku" },
];

// Latest at-limit models, keyed by alias → reset time (Unix-ms or null). Empty
// when there's no usage data, which is the normal case.
let limits = new Map<string, number | null>();

/** Store the host's latest model rate-limit snapshot. Called from the state
 *  message handler. Absent / empty → nothing disabled. */
export function setModelLimits(
  next: { alias: string; resetsAtMs: number | null }[] | undefined
): void {
  const m = new Map<string, number | null>();
  for (const l of next ?? []) m.set(l.alias, l.resetsAtMs);
  limits = m;
}

/** Usage-limit state for an alias: null when the model is available (the
 *  normal, no-data case), otherwise the quiet hint to show next to it —
 *  "resets 14:30" when the bucket carried a reset time, else "at limit".
 *  Shared by the flyout and the new-tab dialog so the treatment can't drift. */
export function modelLimitHint(alias: string): string | null {
  if (!limits.has(alias)) return null;
  const ms = limits.get(alias);
  return ms ? `resets ${formatReset(ms)}` : "at limit";
}

// Lucide-style check glyph, drawn only on the current selection.
const CHECK_PATH = "M20 6L9 17l-5-5";

let openMenu: HTMLElement | null = null;

// Codex's catalogue, ferried with every state push (see StateProjection). It
// is codex's own list, not ours, so a model OpenAI ships tomorrow shows up
// without a Perch release. Empty when codex isn't installed.
let codexOptions: ModelOption[] = [];

/** Store the host's latest codex model catalogue. Called from the state
 *  message handler, alongside setModelLimits. */
export function setCodexModels(
  next: { slug: string; label: string }[] | undefined
): void {
  codexOptions = (next ?? []).map((m) => ({ alias: m.slug, label: m.label }));
}

/** The list a pane's picker offers. Claude's four aliases, or codex's own
 *  catalogue — "Default" first either way, meaning "whatever the agent picks".
 *  Exported for test: which list a pane gets is the whole decision here. */
export function optionsFor(agent: string | undefined): ModelOption[] {
  if (agent !== "codex") return MODEL_OPTIONS;
  return [{ alias: "", label: "Default" }, ...codexOptions];
}

/** Show the model menu anchored under `anchor` for `paneId`, marking
 *  `currentModel` (the pane's selected alias, "" for default). `agent` picks
 *  which catalogue is offered. */
export function showModelMenu(
  anchor: HTMLElement,
  paneId: string,
  currentModel: string,
  agent?: string
): void {
  dismissModelMenu();

  const options = optionsFor(agent);
  // Nothing to choose from (codex installed but its catalogue unreadable):
  // showing an empty popover would read as broken, so show none at all.
  if (options.length <= 1 && agent === "codex") return;

  const menu = document.createElement("div");
  menu.className = "model-menu";
  menu.setAttribute("role", "menu");

  for (const opt of options) {
    // Only fable/opus/sonnet can be limited; default/haiku never appear in the
    // host's limit list, so the hint is null and they're always enabled. The
    // limits are Anthropic's, so a codex model is never annotated.
    const limitHint = agent === "codex" ? null : modelLimitHint(opt.alias);
    const disabled = limitHint !== null;

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "model-menu__item";
    btn.setAttribute("role", "menuitemradio");
    btn.setAttribute("aria-checked", String(opt.alias === currentModel));
    if (disabled) {
      btn.classList.add("model-menu__item--disabled");
      btn.disabled = true;
      btn.setAttribute("aria-disabled", "true");
    }

    // Leading check column (reserved even when empty, so labels stay aligned).
    const check = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    check.setAttribute("class", "model-menu__check");
    check.setAttribute("width", "14");
    check.setAttribute("height", "14");
    check.setAttribute("viewBox", "0 0 24 24");
    check.setAttribute("fill", "none");
    check.setAttribute("stroke", "currentColor");
    check.setAttribute("stroke-width", "2");
    check.setAttribute("stroke-linecap", "round");
    check.setAttribute("stroke-linejoin", "round");
    if (opt.alias === currentModel) {
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      path.setAttribute("d", CHECK_PATH);
      check.appendChild(path);
    }
    btn.appendChild(check);

    const labelEl = document.createElement("span");
    labelEl.className = "model-menu__label";
    labelEl.textContent = opt.label;
    btn.appendChild(labelEl);

    if (limitHint !== null) {
      const hint = document.createElement("span");
      hint.className = "model-menu__hint";
      hint.textContent = limitHint;
      btn.appendChild(hint);
    }

    btn.addEventListener("click", (ev) => {
      ev.stopPropagation();
      if (disabled) return;
      dismissModelMenu();
      if (opt.alias !== currentModel) {
        send({ type: "pane.model", paneId, model: opt.alias });
      }
    });
    menu.appendChild(btn);
  }

  document.body.appendChild(menu);
  openMenu = menu;

  // Anchor below the label, left-aligned; flip to fit the viewport.
  const rect = anchor.getBoundingClientRect();
  menu.style.left = `${rect.left}px`;
  menu.style.top = `${rect.bottom + 6}px`;
  const pr = menu.getBoundingClientRect();
  if (pr.right > window.innerWidth - 8) {
    menu.style.left = `${Math.max(8, window.innerWidth - pr.width - 8)}px`;
  }
  if (pr.bottom > window.innerHeight - 8) {
    menu.style.top = `${Math.max(8, rect.top - pr.height - 6)}px`;
  }

  // Focus the current (or first enabled) item for keyboard nav.
  const items = menu.querySelectorAll<HTMLButtonElement>(
    ".model-menu__item:not(.model-menu__item--disabled)"
  );
  const checked = menu.querySelector<HTMLButtonElement>('[aria-checked="true"]');
  (checked && !checked.disabled ? checked : items[0])?.focus();

  setTimeout(() => {
    document.addEventListener("mousedown", outsideMouseDown, true);
    document.addEventListener("keydown", onKeyDown, true);
  }, 0);
}

export function dismissModelMenu(): void {
  if (!openMenu) return;
  openMenu.remove();
  openMenu = null;
  document.removeEventListener("mousedown", outsideMouseDown, true);
  document.removeEventListener("keydown", onKeyDown, true);
}

function formatReset(ms: number): string {
  try {
    return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  } catch {
    return "";
  }
}

function outsideMouseDown(ev: MouseEvent) {
  if (openMenu && !openMenu.contains(ev.target as Node)) dismissModelMenu();
}

function onKeyDown(ev: KeyboardEvent) {
  if (!openMenu) return;
  if (ev.key === "Escape") {
    ev.preventDefault();
    dismissModelMenu();
    return;
  }
  const items = Array.from(
    openMenu.querySelectorAll<HTMLButtonElement>(
      ".model-menu__item:not(.model-menu__item--disabled)"
    )
  );
  if (items.length === 0) return;
  const focused = document.activeElement as HTMLElement | null;
  const idx = items.indexOf(focused as HTMLButtonElement);
  if (ev.key === "ArrowDown") {
    ev.preventDefault();
    items[(idx + 1 + items.length) % items.length].focus();
  } else if (ev.key === "ArrowUp") {
    ev.preventDefault();
    items[(idx - 1 + items.length) % items.length].focus();
  } else if (ev.key === "Enter" && focused?.classList.contains("model-menu__item")) {
    ev.preventDefault();
    (focused as HTMLButtonElement).click();
  }
}
