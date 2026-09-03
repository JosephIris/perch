// The team room's composer: a textarea that sends on Enter, a typeahead that
// opens on `@`, and the recipient chips above it.
//
// The chips are DERIVED from the text every time it changes (parseMentions),
// never edited on their own — so what you see addressed is exactly what gets
// addressed, and the × on a chip just removes the mention token from the
// text. Same one-source-of-truth rule as the sidebar's aria-pressed toggles.
//
// The popover follows the model-menu idiom: a fixed-position role=listbox,
// outside-mousedown and Esc dismiss, arrow keys to move. It is a body child
// (like every flyout), so it can overhang the room's edges.

import type { TeamBotView } from "./bridge.js";
import { mentionQueryAt, parseMentions, type MentionTarget } from "./mention.js";

export interface Composer {
  element: HTMLElement;
  focus(): void;
  setDraft(text: string): void;
  getDraft(): string;
  /** Re-read the roster (a bot joined or left) and refresh the chips. */
  refresh(): void;
  dispose(): void;
}

export interface ComposerOpts {
  roster: () => TeamBotView[];
  onSend: (text: string, to: MentionTarget, clientId: string) => void;
}

/* The textarea grows with the draft up to this many rows, then scrolls. Six
 * lines is a paragraph; past that the feed above is what should be scrolling. */
const MAX_ROWS = 6;

/** Remove the `@nick` token for `nick` from `text` (first occurrence, word
 *  bounded, case-insensitive), tidying the whitespace it leaves behind. Pure —
 *  the chip's × is this function applied to the draft. */
export function removeMention(text: string, nick: string): string {
  const re = new RegExp(String.raw`(^|[\s(\["'])@${escapeRe(nick)}(?![A-Za-z0-9_-])[ \t]?`, "i");
  return text.replace(re, "$1").replace(/[ \t]{2,}/g, " ").replace(/^[ \t]+/, "").replace(/[ \t]+$/, "");
}

/** The typeahead's candidates for `query`: nicknames starting with it first,
 *  then containing it, then "everyone" — which only offers itself once you've
 *  typed at least its first letter, so an empty `@` shows the team, not a
 *  megaphone. */
export function mentionCandidates(query: string, roster: string[]): string[] {
  const q = query.toLowerCase();
  const starts = roster.filter((n) => n.toLowerCase().startsWith(q));
  const contains = roster.filter((n) => !n.toLowerCase().startsWith(q) && n.toLowerCase().includes(q));
  const out = [...starts, ...contains];
  if (q.length > 0 && "everyone".startsWith(q) && !out.includes("everyone")) out.push("everyone");
  return out;
}

function escapeRe(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function el(tag: string, cls: string, text?: string): HTMLElement {
  const e = document.createElement(tag);
  e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function newClientId(): string {
  try { return crypto.randomUUID(); } catch { /* insecure context */ }
  return `c-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export function buildComposer(opts: ComposerOpts): Composer {
  const root = el("footer", "team-composer");

  // Recipients strip: chips for the resolved mentions, or the "unaddressed"
  // hint. Hidden entirely while the draft is empty so an idle composer is one
  // quiet line.
  const toRow = el("div", "team-composer__to");
  toRow.hidden = true;
  root.appendChild(toRow);

  const row = el("div", "team-composer__row");
  const input = document.createElement("textarea");
  input.className = "team-composer__input";
  input.rows = 1;
  input.placeholder = "Message the team — @ to address a bot";
  input.setAttribute("aria-label", "Message the team");
  input.setAttribute("aria-autocomplete", "list");
  input.setAttribute("aria-expanded", "false");
  input.spellcheck = true;
  row.appendChild(input);

  const sendBtn = document.createElement("button");
  sendBtn.type = "button";
  sendBtn.className = "team-composer__send";
  sendBtn.setAttribute("aria-label", "Send");
  sendBtn.title = "Send (Enter)";
  sendBtn.disabled = true;
  sendBtn.innerHTML =
    '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">' +
    '<path d="M3 8h9M8 4l4 4-4 4" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  row.appendChild(sendBtn);
  root.appendChild(row);

  // ── popover ─────────────────────────────────────────────────────────────
  let pop: HTMLElement | null = null;
  let popItems: HTMLButtonElement[] = [];
  let popIndex = 0;
  let popStart = 0;           // index of the `@` being completed
  let popQueryLen = 0;

  const closePop = () => {
    if (!pop) return;
    pop.remove();
    pop = null;
    popItems = [];
    input.setAttribute("aria-expanded", "false");
    input.removeAttribute("aria-activedescendant");
    document.removeEventListener("mousedown", outsideMouseDown, true);
  };

  const outsideMouseDown = (ev: MouseEvent) => {
    if (pop && !pop.contains(ev.target as Node) && ev.target !== input) closePop();
  };

  const accept = (nick: string) => {
    // Replace `@que` with `@Nick ` and put the caret after it.
    const before = input.value.slice(0, popStart);
    const after = input.value.slice(popStart + 1 + popQueryLen);
    const insert = `@${nick} `;
    input.value = before + insert + after;
    const caret = before.length + insert.length;
    input.setSelectionRange(caret, caret);
    closePop();
    onInput();
    input.focus();
  };

  const highlight = (i: number) => {
    popIndex = (i + popItems.length) % popItems.length;
    popItems.forEach((b, k) => {
      b.setAttribute("aria-selected", String(k === popIndex));
      b.classList.toggle("mention-pop__item--active", k === popIndex);
    });
    input.setAttribute("aria-activedescendant", popItems[popIndex]?.id ?? "");
  };

  const openPop = (start: number, query: string) => {
    const roster = opts.roster();
    const names = roster.map((b) => b.nickname);
    const cands = mentionCandidates(query, names);
    if (cands.length === 0) { closePop(); return; }
    popStart = start;
    popQueryLen = query.length;
    if (!pop) {
      pop = el("div", "mention-pop");
      pop.id = "mention-pop";
      pop.setAttribute("role", "listbox");
      pop.setAttribute("aria-label", "Address a bot");
      document.body.appendChild(pop);
      input.setAttribute("aria-controls", "mention-pop");
      input.setAttribute("aria-expanded", "true");
      setTimeout(() => document.addEventListener("mousedown", outsideMouseDown, true), 0);
    }
    pop.replaceChildren();
    popItems = cands.map((nick, i) => {
      const b = document.createElement("button");
      b.type = "button";
      b.id = `mention-pop-${i}`;
      b.setAttribute("role", "option");
      b.className = "mention-pop__item";
      if (nick === "everyone") {
        b.classList.add("mention-pop__item--all");
        b.appendChild(el("span", "mention-pop__nick", "everyone"));
        b.appendChild(el("span", "mention-pop__hint", "use sparingly"));
      } else {
        const bot = roster.find((x) => x.nickname === nick);
        b.appendChild(el("span", "mention-pop__nick", nick));
        if (bot) b.appendChild(el("span", "mention-pop__pos", bot.positionName));
      }
      // mousedown, not click: click would land after the textarea's blur and
      // the outside-mousedown dismissal raced it.
      b.addEventListener("mousedown", (ev) => { ev.preventDefault(); accept(nick); });
      pop!.appendChild(b);
      return b;
    });
    highlight(0);
    // Anchor above the textarea's left edge; flip below if there's no room.
    const r = input.getBoundingClientRect();
    pop.style.left = `${r.left}px`;
    pop.style.top = "0px";
    const pr = pop.getBoundingClientRect();
    let top = r.top - pr.height - 6;
    if (top < 8) top = r.bottom + 6;
    pop.style.top = `${top}px`;
    if (pr.right > window.innerWidth - 8)
      pop.style.left = `${Math.max(8, window.innerWidth - pr.width - 8)}px`;
  };

  // ── chips ───────────────────────────────────────────────────────────────
  const renderTo = () => {
    const text = input.value;
    toRow.replaceChildren();
    if (text.trim().length === 0) { toRow.hidden = true; return; }
    toRow.hidden = false;
    const names = opts.roster().map((b) => b.nickname);
    const { to } = parseMentions(text, names);
    if (to === null) {
      toRow.appendChild(el("span", "team-composer__route", "No one tagged — goes to everyone; a bot answers only if it's for them"));
      return;
    }
    const chipFor = (label: string, nick: string | null) => {
      const chip = el("span", "to-chip" + (nick === null ? " to-chip--all" : ""));
      chip.appendChild(el("span", "to-chip__label", `@${label}`));
      const x = document.createElement("button");
      x.type = "button";
      x.className = "to-chip__x";
      x.setAttribute("aria-label", `Remove ${label}`);
      x.textContent = "×";
      x.addEventListener("click", () => {
        input.value = removeMention(input.value, nick ?? "everyone");
        if (nick === null) input.value = removeMention(input.value, "all");
        onInput();
        input.focus();
      });
      chip.appendChild(x);
      return chip;
    };
    if (to === "everyone") toRow.appendChild(chipFor("everyone", null));
    else for (const nick of to) toRow.appendChild(chipFor(nick, nick));
  };

  const grow = () => {
    input.style.height = "auto";
    const line = parseFloat(getComputedStyle(input).lineHeight) || 20;
    const pad = input.offsetHeight - input.clientHeight;
    const max = line * MAX_ROWS + pad + 12;
    input.style.height = `${Math.min(input.scrollHeight, max)}px`;
    input.style.overflowY = input.scrollHeight > max ? "auto" : "hidden";
  };

  const onInput = () => {
    sendBtn.disabled = input.value.trim().length === 0;
    renderTo();
    grow();
    const q = mentionQueryAt(input.value, input.selectionStart ?? input.value.length);
    if (q) openPop(q.start, q.query); else closePop();
  };

  const submit = () => {
    const text = input.value.trim();
    if (text.length === 0) return;
    const names = opts.roster().map((b) => b.nickname);
    const { to } = parseMentions(text, names);
    opts.onSend(text, to, newClientId());
    input.value = "";
    closePop();
    onInput();
  };

  input.addEventListener("input", onInput);
  input.addEventListener("click", () => {
    const q = mentionQueryAt(input.value, input.selectionStart ?? 0);
    if (q) openPop(q.start, q.query); else closePop();
  });
  input.addEventListener("keydown", (ev) => {
    // Nothing typed here may reach xterm or the global chords. Ctrl/Alt combos
    // are left alone so Ctrl+B (sidebar) and friends still work from the room.
    if (!ev.ctrlKey && !ev.altKey && !ev.metaKey) ev.stopPropagation();
    if (pop) {
      if (ev.key === "ArrowDown") { ev.preventDefault(); highlight(popIndex + 1); return; }
      if (ev.key === "ArrowUp") { ev.preventDefault(); highlight(popIndex - 1); return; }
      if (ev.key === "Enter" || ev.key === "Tab") {
        ev.preventDefault();
        const nick = popItems[popIndex]?.querySelector(".mention-pop__nick")?.textContent ?? "";
        if (nick) accept(nick);
        return;
      }
      if (ev.key === "Escape") { ev.preventDefault(); ev.stopPropagation(); closePop(); return; }
    }
    if (ev.key === "Enter" && !ev.shiftKey) {
      ev.preventDefault();
      submit();
    }
  });
  input.addEventListener("keyup", (ev) => {
    // Caret moved by arrow keys — the popover follows the token under it.
    if (ev.key === "ArrowLeft" || ev.key === "ArrowRight" || ev.key === "Home" || ev.key === "End") {
      const q = mentionQueryAt(input.value, input.selectionStart ?? 0);
      if (q) openPop(q.start, q.query); else closePop();
    }
  });
  sendBtn.addEventListener("click", submit);

  return {
    element: root,
    focus: () => input.focus(),
    setDraft: (t) => { input.value = t; onInput(); },
    getDraft: () => input.value,
    refresh: () => { renderTo(); },
    dispose: () => { closePop(); root.remove(); },
  };
}
