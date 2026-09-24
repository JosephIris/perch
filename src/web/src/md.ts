// A small Markdown renderer for Claude's replies in the project chat.
//
// Deliberately small and DOM-built: every piece of text lands in a text node,
// never in innerHTML, so nothing Claude writes can become markup. It covers
// what Claude's answers actually use — paragraphs, headings, lists, fenced
// code, quotes, tables (kept as monospace text), inline code, bold, italic
// and links — and renders anything else as the plain text it is.

import { send } from "./bridge.js";

export type Block =
  | { kind: "p"; text: string }
  | { kind: "h"; level: number; text: string }
  | { kind: "ul" | "ol"; items: string[] }
  | { kind: "code"; text: string }
  | { kind: "quote"; text: string };

/** Split Markdown into blocks. Pure, so it can be tested without a DOM. */
export function parseBlocks(src: string): Block[] {
  const lines = (src ?? "").replace(/\r\n?/g, "\n").split("\n");
  const out: Block[] = [];
  let para: string[] = [];
  const flush = () => {
    if (para.length) out.push({ kind: "p", text: para.join("\n") });
    para = [];
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fence = line.match(/^\s*```/);
    if (fence) {
      flush();
      const body: string[] = [];
      for (i++; i < lines.length && !/^\s*```/.test(lines[i]); i++) body.push(lines[i]);
      out.push({ kind: "code", text: body.join("\n") });
      continue;
    }
    if (/^\s*\|.*\|\s*$/.test(line)) {
      // A table: keep its rows as aligned monospace text.
      flush();
      const rows: string[] = [];
      for (; i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i]); i++)
        if (!/^\s*\|[\s:|-]+\|\s*$/.test(lines[i])) rows.push(lines[i].trim());
      i--;
      out.push({ kind: "code", text: rows.join("\n") });
      continue;
    }
    const h = line.match(/^(#{1,4})\s+(.*)$/);
    if (h) { flush(); out.push({ kind: "h", level: h[1].length, text: h[2].trim() }); continue; }
    const bullet = /^\s*[-*+]\s+(.*)$/;
    const numbered = /^\s*\d+[.)]\s+(.*)$/;
    if (bullet.test(line) || numbered.test(line)) {
      flush();
      const kind = bullet.test(line) ? "ul" : "ol";
      const rx = kind === "ul" ? bullet : numbered;
      const items: string[] = [];
      for (; i < lines.length; i++) {
        const m = lines[i].match(rx);
        if (m) items.push(m[1]);
        else if (/^\s{2,}\S/.test(lines[i]) && items.length) items[items.length - 1] += " " + lines[i].trim();
        else break;
      }
      i--;
      out.push({ kind, items });
      continue;
    }
    if (/^\s*>\s?/.test(line)) {
      flush();
      const q: string[] = [];
      for (; i < lines.length && /^\s*>\s?/.test(lines[i]); i++) q.push(lines[i].replace(/^\s*>\s?/, ""));
      i--;
      out.push({ kind: "quote", text: q.join("\n") });
      continue;
    }
    if (line.trim() === "") { flush(); continue; }
    para.push(line);
  }
  flush();
  return out;
}

export type Span =
  | { kind: "text" | "code" | "bold" | "italic"; text: string }
  | { kind: "link"; text: string; href: string };

const INLINE = /(`[^`\n]+`)|(\*\*[^*\n]+\*\*)|(\[[^\]\n]+\]\((https?:\/\/[^)\s]+)\))|(\*[^*\s\n][^*\n]*\*)|(\b_[^_\n]+_\b)/g;

/** Inline spans of one block's text. Pure. */
export function parseInline(text: string): Span[] {
  const out: Span[] = [];
  let at = 0;
  INLINE.lastIndex = 0;
  for (let m = INLINE.exec(text); m; m = INLINE.exec(text)) {
    if (m.index > at) out.push({ kind: "text", text: text.slice(at, m.index) });
    const t = m[0];
    if (m[1]) out.push({ kind: "code", text: t.slice(1, -1) });
    else if (m[2]) out.push({ kind: "bold", text: t.slice(2, -2) });
    else if (m[3]) out.push({ kind: "link", text: t.slice(1, t.indexOf("](")), href: m[4] });
    else out.push({ kind: "italic", text: t.slice(1, -1) });
    at = m.index + t.length;
  }
  if (at < text.length) out.push({ kind: "text", text: text.slice(at) });
  return out;
}

/** Plain text with things drawn in it — a project chat's "#3" becomes that
 *  thread's tag. Text goes in as text nodes either way. */
export type TextDecorator = (host: HTMLElement, text: string) => void;

function inline(host: HTMLElement, text: string, deco?: TextDecorator) {
  for (const s of parseInline(text)) {
    if (s.kind === "text") { if (deco) deco(host, s.text); else host.append(s.text); continue; }
    if (s.kind === "link") {
      const a = document.createElement("a");
      a.className = "md-link";
      a.textContent = s.text;
      a.href = s.href;
      a.title = s.href;
      a.addEventListener("click", (ev) => { ev.preventDefault(); send({ type: "url.open", url: s.href }); });
      host.appendChild(a);
      continue;
    }
    const el = document.createElement(s.kind === "code" ? "code" : s.kind === "bold" ? "strong" : "em");
    if (s.kind === "code") el.className = "md-code";
    if (s.kind !== "code" && deco) deco(el, s.text); else el.textContent = s.text;
    host.appendChild(el);
  }
}

/** Markdown → a fragment of safe DOM. */
export function renderMarkdown(src: string, deco?: TextDecorator): DocumentFragment {
  const frag = document.createDocumentFragment();
  for (const b of parseBlocks(src)) {
    let el: HTMLElement;
    switch (b.kind) {
      case "p": el = document.createElement("p"); inline(el, b.text, deco); break;
      case "h": el = document.createElement("div"); el.className = `md-h md-h${b.level}`; inline(el, b.text, deco); break;
      case "code": {
        el = document.createElement("pre"); el.className = "md-pre";
        const c = document.createElement("code"); c.textContent = b.text; el.appendChild(c);
        break;
      }
      case "quote": el = document.createElement("blockquote"); el.className = "md-quote"; inline(el, b.text, deco); break;
      default: {
        el = document.createElement(b.kind);
        el.className = "md-list";
        for (const it of b.items) { const li = document.createElement("li"); inline(li, it, deco); el.appendChild(li); }
      }
    }
    frag.appendChild(el);
  }
  return frag;
}
