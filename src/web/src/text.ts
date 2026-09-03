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
// Three tiers, smallest first:
//   appendInline — **bold** and `code`, nothing else. The 336px journal rail.
//   appendRich   — the above plus links, image-path chips and @mention chips,
//                  as one run of prose. Short room rows (a pending post).
//   appendBlocks — the room's message bodies: parseBlocks() splits the text
//                  into paragraphs, lists, headings, fences, quotes, rules and
//                  markdown TABLES, and each block's prose goes through the
//                  same inline pass (plus italic, strike and [label](url)).
//                  A bot's status report is a document, and reading one as a
//                  single grey slab of pre-wrap is what made the room tiring.
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

/** A clickable link. `label` is a [text](url) label — the URL itself stays in
 *  the tooltip, so what a link points at is always one hover away. */
function linkNode(url: string, label?: string): HTMLElement {
  const a = document.createElement("a");
  a.className = "tf-link";
  a.href = url;
  a.rel = "noopener";
  a.textContent = label ?? url;
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

// ---- Block markdown: the parser (pure) -------------------------------------
//
// A bot's message is a small document: paragraphs, bullets, a fenced snippet,
// now and then a table. The parser turns the text into blocks and nothing
// else — no DOM, no escaping decisions — so every rule here is unit-tested
// (test/team-text.test.ts) and the renderer below stays a dumb walk.
//
// Deliberately NOT supported, because the bots here write code and column
// names: _underscore italics_. `lifetime_pbundle_avg_lossprice_fixed` is a
// column, not emphasis, and rows of those are the room's daily bread.

/** Column alignment as a table's separator row asks for it. */
export type Align = "left" | "center" | "right";

/** One bullet, with at most one level of children under it. */
export type ListItem = { text: string; items: ListItem[] };

/** A block of a message body. `para` and `quote` keep their lines apart: a
 *  single newline inside a paragraph is a hard break (bots line-break their
 *  reports on purpose), a blank line starts a new paragraph. */
export type Block =
  | { kind: "para"; lines: string[] }
  | { kind: "head"; level: number; text: string }
  | { kind: "list"; ordered: boolean; items: ListItem[] }
  | { kind: "code"; lang: string; lines: string[] }
  | { kind: "quote"; lines: string[] }
  | { kind: "table"; head: string[]; align: Align[]; rows: string[][] }
  | { kind: "rule" };

const FENCE_RE = /^\s{0,3}(`{3,}|~{3,})\s*([^\s`]*)\s*$/;
const FENCE_END_RE = /^\s{0,3}(?:`{3,}|~{3,})\s*$/;
const HEAD_RE = /^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$/;
const QUOTE_RE = /^\s{0,3}>\s?(.*)$/;
const RULE_RE = /^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$/;
/* A bullet or a number, then a space, then something. The space and the
 * something are what keep a lone "-" line and an em-dash aside out of the
 * list parser. */
const LIST_RE = /^(\s*)(?:([-*•])|(\d{1,9})[.)])\s+(.+)$/;
/* Every cell of a separator row: --- :--- ---: :---: (two dashes minimum, so
 * a single "-" cell can't turn a sentence with pipes into a table). */
const SEP_CELL_RE = /^:?-{2,}:?$/;

/** Split a table row into cells: one optional pipe each side, `\|` escaped. */
function splitCells(line: string): string[] {
  let s = line.trim();
  if (s.startsWith("|")) s = s.slice(1);
  if (s.endsWith("|") && !s.endsWith("\\|")) s = s.slice(0, -1);
  const cells: string[] = [];
  let cur = "";
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === "\\" && s[i + 1] === "|") { cur += "|"; i++; continue; }
    if (c === "|") { cells.push(cur.trim()); cur = ""; continue; }
    cur += c;
  }
  cells.push(cur.trim());
  return cells;
}

/** The alignments a separator row asks for, or null when these two lines
 *  aren't a table head: same column count, every separator cell a run of
 *  dashes. */
function tableAlign(headLine: string, sepLine: string): Align[] | null {
  if (!headLine.includes("|") || !sepLine.includes("-")) return null;
  const head = splitCells(headLine);
  const sep = splitCells(sepLine);
  if (sep.length !== head.length || sep.length === 0) return null;
  const align: Align[] = [];
  for (const cell of sep) {
    if (!SEP_CELL_RE.test(cell)) return null;
    const left = cell.startsWith(":"), right = cell.endsWith(":");
    align.push(right && left ? "center" : right ? "right" : "left");
  }
  return align;
}

/** True when `line` (with `next` after it) opens a block a paragraph must
 *  stop before. */
function startsBlock(line: string, next: string | undefined): boolean {
  if (line.trim() === "") return true;
  if (FENCE_RE.test(line) || HEAD_RE.test(line) || QUOTE_RE.test(line)) return true;
  if (RULE_RE.test(line) || LIST_RE.test(line)) return true;
  return tableAlign(line, next ?? "") !== null;
}

/** Build the items of one list run — indent decides nesting, and only one
 *  level of it: a bot's third level is noise the room doesn't need. */
function listItems(lines: string[], base: number): ListItem[] {
  const items: ListItem[] = [];
  for (const line of lines) {
    const m = LIST_RE.exec(line);
    if (!m) {                                   // a wrapped continuation line
      const into = items[items.length - 1];
      if (!into) continue;
      const target = into.items[into.items.length - 1] ?? into;
      target.text = `${target.text} ${line.trim()}`;
      continue;
    }
    const item: ListItem = { text: m[4].trim(), items: [] };
    if (m[1].length >= base + 2 && items.length > 0) items[items.length - 1].items.push(item);
    else items.push(item);
  }
  return items;
}

/** Split a message body into blocks. Pure; the renderer below walks the
 *  result. Unknown syntax stays literal text — this is a chat feed, not a
 *  markdown viewer, and a wrong guess reads worse than a plain line. */
export function parseBlocks(text: string): Block[] {
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  const out: Block[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];

    // A fence swallows everything up to its close — pipes and hashes inside
    // are code, not a table and not a heading.
    const fence = FENCE_RE.exec(line);
    if (fence) {
      const body: string[] = [];
      i++;
      while (i < lines.length && !FENCE_END_RE.test(lines[i])) { body.push(lines[i]); i++; }
      if (i < lines.length) i++;                // the closing fence
      out.push({ kind: "code", lang: fence[2] ?? "", lines: body });
      continue;
    }

    if (line.trim() === "") { i++; continue; }

    const head = HEAD_RE.exec(line);
    if (head) { out.push({ kind: "head", level: Math.min(head[1].length, 4), text: head[2] }); i++; continue; }

    if (QUOTE_RE.test(line)) {
      const body: string[] = [];
      while (i < lines.length) {
        const q = QUOTE_RE.exec(lines[i]);
        if (!q) break;
        body.push(q[1]); i++;
      }
      out.push({ kind: "quote", lines: body });
      continue;
    }

    // A table, but only when the line under the head is a real separator row.
    const align = tableAlign(line, lines[i + 1] ?? "");
    if (align) {
      const headCells = splitCells(line);
      const rows: string[][] = [];
      i += 2;
      while (i < lines.length && lines[i].trim() !== "" && lines[i].includes("|") && !FENCE_RE.test(lines[i])) {
        const cells = splitCells(lines[i]);
        while (cells.length < headCells.length) cells.push("");
        rows.push(cells.slice(0, headCells.length));
        i++;
      }
      out.push({ kind: "table", head: headCells, align, rows });
      continue;
    }

    if (RULE_RE.test(line)) { out.push({ kind: "rule" }); i++; continue; }

    const first = LIST_RE.exec(line);
    if (first) {
      const base = first[1].length;
      const ordered = first[3] !== undefined;
      const run: string[] = [];
      while (i < lines.length) {
        const cur = lines[i];
        if (cur.trim() === "") break;
        const m = LIST_RE.exec(cur);
        if (m) {
          if (m[1].length < base + 2 && (m[3] !== undefined) !== ordered) break;  // numbers after bullets: a new list
          run.push(cur); i++; continue;
        }
        if (/^\s{2,}\S/.test(cur)) { run.push(cur); i++; continue; }              // a wrapped line
        break;
      }
      out.push({ kind: "list", ordered, items: listItems(run, base) });
      continue;
    }

    const para: string[] = [line];
    i++;
    while (i < lines.length && !startsBlock(lines[i], lines[i + 1])) { para.push(lines[i]); i++; }
    out.push({ kind: "para", lines: para });
  }
  return out;
}

// ---- Block markdown: the renderer ------------------------------------------

/* Inline forms, tried in this order at each position: code first (so a path
 * or a mention inside backticks stays literal), then bold, strike, a
 * [label](url), then single-asterisk italic — no space just inside the stars,
 * which keeps "2 * 3 * 4" out of it. */
const INLINE_RE =
  /`([^`]+)`|\*\*([^\n]+?)\*\*|~~([^\n]+?)~~|\[([^\]\n]{1,200})\]\((https?:\/\/[^\s)]+)\)|\*(?!\s)([^*\n]+?)(?<!\s)\*(?!\*)/g;

const el = (tag: string, cls: string): HTMLElement => {
  const e = document.createElement(tag);
  e.className = cls;
  return e;
};

/** One run of prose: the inline markdown, then links, image paths and
 *  @mentions in what's left. Image paths it meets are pushed to `images`. */
function appendInlineRich(host: HTMLElement, text: string, roster: string[], images: string[]): void {
  INLINE_RE.lastIndex = 0;
  let last = 0;
  for (let m = INLINE_RE.exec(text); m; m = INLINE_RE.exec(text)) {
    if (m.index > last) appendPlain(host, text.slice(last, m.index), roster, images);
    if (m[1] !== undefined) {
      const code = m[1];
      host.appendChild(elText("code", "beat__code", code));
      for (const im of findImagePaths(code)) if (im.start === 0 && im.end === code.length) images.push(im.value);
    } else if (m[2] !== undefined) appendPlain(host.appendChild(el("strong", "beat__strong")), m[2], roster, images);
    else if (m[3] !== undefined) appendPlain(host.appendChild(el("span", "md-del")), m[3], roster, images);
    else if (m[4] !== undefined) host.appendChild(linkNode(m[5], m[4]));
    else appendPlain(host.appendChild(el("em", "md-em")), m[6], roster, images);
    last = m.index + m[0].length;
  }
  if (last < text.length) appendPlain(host, text.slice(last), roster, images);
}

/** Lines of one paragraph or quote: a single newline is a hard break. */
function appendLines(host: HTMLElement, lines: string[], roster: string[], images: string[]): void {
  lines.forEach((line, n) => {
    if (n > 0) host.appendChild(document.createElement("br"));
    appendInlineRich(host, line, roster, images);
  });
}

function renderList(ordered: boolean, items: ListItem[], roster: string[], images: string[], sub: boolean): HTMLElement {
  const list = el(ordered ? "ol" : "ul", `md-list${ordered ? " md-list--num" : ""}${sub ? " md-list--sub" : ""}`);
  for (const item of items) {
    const li = el("li", "md-li");
    appendInlineRich(li, item.text, roster, images);
    if (item.items.length > 0) li.appendChild(renderList(ordered, item.items, roster, images, true));
    list.appendChild(li);
  }
  return list;
}

function renderTable(block: Extract<Block, { kind: "table" }>, roster: string[], images: string[]): HTMLElement {
  const wrap = el("div", "md-tablewrap");
  const table = el("table", "md-table");
  const thead = document.createElement("thead");
  const headRow = document.createElement("tr");
  block.head.forEach((cell, n) => {
    const th = el("th", `md-th md-th--${block.align[n] ?? "left"}`);
    appendInlineRich(th, cell, roster, images);
    headRow.appendChild(th);
  });
  thead.appendChild(headRow);
  table.appendChild(thead);
  const tbody = document.createElement("tbody");
  for (const row of block.rows) {
    const tr = document.createElement("tr");
    row.forEach((cell, n) => {
      const td = el("td", `md-td md-td--${block.align[n] ?? "left"}`);
      appendInlineRich(td, cell, roster, images);
      tr.appendChild(td);
    });
    tbody.appendChild(tr);
  }
  table.appendChild(tbody);
  wrap.appendChild(table);
  return wrap;
}

/** Render a message body as blocks: paragraphs that breathe, real bullets, a
 *  fence that scrolls inside its own box, a real table. Returns the image
 *  paths it saw, in order, so the caller can hang thumbnails under the text —
 *  the same contract as appendRich, so one is a drop-in for the other.
 *
 *  The host is marked `md` (and `md--rich` when the body is more than plain
 *  paragraphs) so the stylesheet can drop pre-wrap and clamp by height rather
 *  than by line count. Nothing goes through innerHTML: this is agent output,
 *  and agent output is never handed to an HTML parser. */
export function appendBlocks(host: HTMLElement, text: string, roster: string[]): string[] {
  const images: string[] = [];
  const blocks = parseBlocks(text);
  host.classList.add("md");
  if (blocks.some((b) => b.kind !== "para")) host.classList.add("md--rich");
  for (const block of blocks) {
    switch (block.kind) {
      case "para": {
        const p = el("p", "md-p");
        appendLines(p, block.lines, roster, images);
        host.appendChild(p);
        break;
      }
      case "head": {
        const h = el("div", `md-h md-h--${block.level}`);
        h.setAttribute("role", "heading");
        h.setAttribute("aria-level", String(Math.min(block.level + 2, 6)));
        appendInlineRich(h, block.text, roster, images);
        host.appendChild(h);
        break;
      }
      case "list":
        host.appendChild(renderList(block.ordered, block.items, roster, images, false));
        break;
      case "code": {
        const pre = el("pre", "md-pre");
        if (block.lang) pre.dataset.lang = block.lang;
        pre.appendChild(elText("code", "md-code", block.lines.join("\n")));
        host.appendChild(pre);
        break;
      }
      case "quote": {
        const q = el("blockquote", "md-quote");
        appendLines(q, block.lines, roster, images);
        host.appendChild(q);
        break;
      }
      case "table":
        host.appendChild(renderTable(block, roster, images));
        break;
      case "rule":
        host.appendChild(el("hr", "md-hr"));
        break;
    }
  }
  return images;
}
