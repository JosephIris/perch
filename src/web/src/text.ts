// Text rendering shared by the journal rail and the team room: the local
// wall-clock stamp and the two bits of inline markdown an agent actually uses
// in prose. Lifted out of inspector.ts the moment a second surface needed the
// same tokenizer — one renderer, so a beat reads the same in both places.
//
// The room's richer renderer (appendRich) also turns URLs into links and an
// absolute path to a screenshot into a quiet path chip; the caller renders the
// thumbnails it returns. The rail keeps appendInline: a 336px journal is not
// the place for pictures.
//
// Everything here builds DOM nodes; nothing goes through innerHTML. Transcript
// text is agent output, and agent output is not something we hand to an HTML
// parser.

import { send } from "./bridge.js";
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

// ---- Links and image paths (pure) ------------------------------------------

/** A span of the text that is a URL or an image path, with what it is. */
export type TextSpan = { start: number; end: number; kind: "link" | "image"; value: string };

/* A URL as an agent writes one in prose: http(s), up to whitespace or a
 * closing bracket, with trailing punctuation left to the sentence. */
const LINK_RE = /https?:\/\/[^\s<>"'`\]]+/g;
/* An absolute path to a picture: Windows drive or forward-slash rooted (the
 * slash must start a token, so "relative/x.png" is not "/x.png"), no spaces
 * (a bot quotes spaced paths in backticks, which are cut out before this
 * runs anyway), ending in an image extension. */
const IMAGE_RE = /(?<![\w.\\/-])(?:[A-Za-z]:[\\/]|\/)[^\s<>"'`]*?\.(?:png|jpe?g|gif|webp)\b/gi;

/** Trim the trailing punctuation a sentence leaves on a URL ("…/x)." → the
 *  paren only closes when one was opened inside the link). */
function trimLink(url: string): string {
  let u = url;
  for (;;) {
    const last = u[u.length - 1];
    if (last === undefined) break;
    if (".,;:!?".includes(last)) { u = u.slice(0, -1); continue; }
    if (last === ")" && (u.match(/\(/g)?.length ?? 0) < (u.match(/\)/g)?.length ?? 0)) { u = u.slice(0, -1); continue; }
    break;
  }
  return u;
}

/** Every http(s) URL in `text`, in order. Exported for tests. */
export function findLinks(text: string): TextSpan[] {
  const out: TextSpan[] = [];
  LINK_RE.lastIndex = 0;
  for (let m = LINK_RE.exec(text); m; m = LINK_RE.exec(text)) {
    const value = trimLink(m[0]);
    if (value.length < 10) continue;
    out.push({ start: m.index, end: m.index + value.length, kind: "link", value });
  }
  return out;
}

/** Every absolute image path in `text` (Windows `C:\…` or `/…`, .png .jpg
 *  .jpeg .gif .webp), in order. Exported for tests. */
export function findImagePaths(text: string): TextSpan[] {
  const out: TextSpan[] = [];
  IMAGE_RE.lastIndex = 0;
  for (let m = IMAGE_RE.exec(text); m; m = IMAGE_RE.exec(text)) {
    out.push({ start: m.index, end: m.index + m[0].length, kind: "image", value: m[0] });
  }
  return out;
}

/** The link and image spans of a segment, merged in text order (links win an
 *  overlap — a URL that ends in .png is a link). */
function findSpans(text: string): TextSpan[] {
  const links = findLinks(text);
  const images = findImagePaths(text).filter((im) => !links.some((l) => im.start < l.end && l.start < im.end));
  return [...links, ...images].sort((a, b) => a.start - b.start);
}

/** The last path segment, for a chip: "kpi-loading-bdm-dark.png". */
export function imageLabel(path: string): string {
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] || path;
}

// ---- The room's renderer ---------------------------------------------------

/** appendInline plus @mention chips, clickable links and image-path chips.
 *  A token matching a roster nickname becomes a chip and `@everyone` a group
 *  chip; anything else — `@foo` nobody on the team is called, an email address
 *  — stays literal, because a chip claims the message reached someone and we
 *  only claim that for a real recipient. Bold and code spans are cut first,
 *  so a mention or a path inside `code` stays code.
 *
 *  Returns the image paths it saw, so the caller can hang thumbnails under
 *  the text (the text keeps a quiet chip where the path was). */
export function appendRich(host: HTMLElement, text: string, roster: string[]): string[] {
  const images: string[] = [];
  const re = /\*\*([^*]+)\*\*|`([^`]+)`/g;
  let last = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (m.index > last) appendPlain(host, text.slice(last, m.index), roster, images);
    if (m[1] !== undefined) host.appendChild(elText("strong", "beat__strong", m[1]));
    else {
      // A path in backticks is still a picture the bot means to show.
      const code = m[2];
      host.appendChild(elText("code", "beat__code", code));
      for (const im of findImagePaths(code)) if (im.start === 0 && im.end === code.length) images.push(im.value);
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) appendPlain(host, text.slice(last), roster, images);
  return images;
}

function appendPlain(host: HTMLElement, segment: string, roster: string[], images: string[]): void {
  let last = 0;
  for (const span of findSpans(segment)) {
    if (span.start > last) appendMentions(host, segment.slice(last, span.start), roster);
    if (span.kind === "link") host.appendChild(linkNode(span.value));
    else {
      host.appendChild(elText("span", "tf-path", imageLabel(span.value))).title = span.value;
      images.push(span.value);
    }
    last = span.end;
  }
  if (last < segment.length) appendMentions(host, segment.slice(last), roster);
}

function linkNode(url: string): HTMLElement {
  const a = document.createElement("a");
  a.className = "tf-link";
  a.href = url;
  a.rel = "noopener";
  a.textContent = url;
  a.title = url;
  a.addEventListener("click", (ev) => {
    ev.preventDefault();
    ev.stopPropagation();
    send({ type: "url.open", url });
  });
  return a;
}

function appendMentions(host: HTMLElement, segment: string, roster: string[]): void {
  for (const t of tokenizeMentions(segment, roster)) {
    if (t.kind === "text") host.append(t.text);
    else if (t.kind === "everyone") host.appendChild(elText("span", "tf-mention tf-mention--all", t.text));
    else host.appendChild(elText("span", "tf-mention", t.text));
  }
}
