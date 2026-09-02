// Text rendering shared by the journal rail and the team room: the local
// wall-clock stamp and the two bits of inline markdown an agent actually uses
// in prose. Lifted out of inspector.ts the moment a second surface needed the
// same tokenizer — one renderer, so a beat reads the same in both places.
//
// Everything here builds DOM nodes; nothing goes through innerHTML. Transcript
// text is agent output, and agent output is not something we hand to an HTML
// parser.

import { tokenizeMentions } from "./mention.js";

const elText = (tag: string, cls: string, text: string): HTMLElement => {
  const e = document.createElement(tag);
  e.className = cls;
  e.textContent = text;
  return e;
};

/** Local wall-clock "19:32" — the journal is read against the terminal beside
 *  it, and the terminal shows local time. */
export function hhmm(iso: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return "";
  const d = new Date(t);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

/** Render the two bits of inline markdown an agent actually uses in prose:
 *  **bold** and `code`. Left raw, they're pure noise — a beat reads as
 *  "**Pushed to home-tools** (branch `user-cache-restrict`)", asterisks and all,
 *  which is worse than either rendering them or stripping them.
 *
 *  Tokenized into DOM nodes rather than assigned as innerHTML: transcript text
 *  is agent output, and agent output is not something we hand to an HTML parser.
 *  Anything more than these two forms (headings, lists, links) stays literal —
 *  a 336px rail is not a markdown viewer. */
export function appendInline(host: HTMLElement, text: string): void {
  const re = /\*\*([^*]+)\*\*|`([^`]+)`/g;
  let last = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (m.index > last) host.append(text.slice(last, m.index));
    if (m[1] !== undefined) host.appendChild(elText("strong", "beat__strong", m[1]));
    else host.appendChild(elText("code", "beat__code", m[2]));
    last = m.index + m[0].length;
  }
  if (last < text.length) host.append(text.slice(last));
}

/** appendInline plus @mention chips. A token matching a roster nickname
 *  becomes a chip and `@everyone` a group chip; anything else — `@foo` nobody
 *  on the team is called, an email address — stays literal, because a chip
 *  claims the message reached someone and we only claim that for a real
 *  recipient. Bold and code spans are cut first, so a mention inside `code`
 *  stays code. */
export function appendRich(host: HTMLElement, text: string, roster: string[]): void {
  const re = /\*\*([^*]+)\*\*|`([^`]+)`/g;
  let last = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (m.index > last) appendMentions(host, text.slice(last, m.index), roster);
    if (m[1] !== undefined) host.appendChild(elText("strong", "beat__strong", m[1]));
    else host.appendChild(elText("code", "beat__code", m[2]));
    last = m.index + m[0].length;
  }
  if (last < text.length) appendMentions(host, text.slice(last), roster);
}

function appendMentions(host: HTMLElement, segment: string, roster: string[]): void {
  for (const t of tokenizeMentions(segment, roster)) {
    if (t.kind === "text") host.append(t.text);
    else if (t.kind === "everyone") host.appendChild(elText("span", "tf-mention tf-mention--all", t.text));
    else host.appendChild(elText("span", "tf-mention", t.text));
  }
}
