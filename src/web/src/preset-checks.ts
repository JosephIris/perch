// Project chat presets as a list of checks: one per `.perch/presets/*.md` in
// the project's repo, its name with its first line as the hint. Shared by the
// new-project-chat dialog and the chat Overview's About tab. Same drawn
// checkbox card as the new-tab dialog's worktree choice.

import type { PresetView } from "./bridge.js";

const TICK =
  '<svg viewBox="0 0 12 12" width="12" height="12" fill="none">' +
  '<path d="M2.5 6.2 L4.8 8.5 L9.5 3.5" stroke="currentColor" stroke-width="1.6" ' +
  'stroke-linecap="round" stroke-linejoin="round"/></svg>';

export type PresetChecks = { element: HTMLElement; selected: () => string[] };

export function presetChecks(presets: PresetView[], active: string[] = []): PresetChecks {
  const list = document.createElement("div");
  list.className = "preset-checks";
  const on = new Set(active.map((s) => s.toLowerCase()));
  const inputs: { slug: string; input: HTMLInputElement }[] = [];
  for (const p of presets) {
    const row = document.createElement("label");
    row.className = "newtab-check";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "newtab-check__input";
    input.checked = on.has(p.slug.toLowerCase());
    const box = document.createElement("span");
    box.className = "newtab-check__box";
    box.setAttribute("aria-hidden", "true");
    box.innerHTML = TICK;
    const text = document.createElement("span");
    text.className = "newtab-check__text";
    const name = document.createElement("span");
    name.className = "newtab-check__label";
    name.textContent = p.name;
    text.appendChild(name);
    if (p.summary) {
      const hint = document.createElement("span");
      hint.className = "newtab-check__hint";
      hint.textContent = p.summary;
      text.appendChild(hint);
    }
    row.title = `.perch/presets/${p.slug}.md`;
    row.append(input, box, text);
    const sync = () => { row.dataset.checked = String(input.checked); };
    input.addEventListener("change", sync);
    sync();
    list.appendChild(row);
    inputs.push({ slug: p.slug, input });
  }
  return { element: list, selected: () => inputs.filter((i) => i.input.checked).map((i) => i.slug) };
}
