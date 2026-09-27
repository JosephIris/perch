// Screenshots a thread took, shown where it talks about them. Threads save a
// screenshot to a file and name it — "Screenshot: `C:/tmp/mag-top5.png`",
// "Screens: C:/tmp/a_56.png, a_57.png" — and the chat passes the path on as
// text. Under any message that names picture files, a strip of them: the page
// can't read local files, so each is asked of the host (chat.image) once and
// kept; a click shows it full size.

import { send } from "./bridge.js";
import type { ChatImageDataMessage } from "./bridge.js";
import { el } from "./thread-ui.js";

const EXT = "png|jpe?g|gif|webp";
const ABS = new RegExp(String.raw`(?:[A-Za-z]:[\\/]|\\\\|/(?=[\w.~-]))[^\s\`'"<>|*?,;()\[\]]*?\.(?:${EXT})(?![\w])`, "gi");
const BARE = new RegExp(String.raw`(?<![\\/\w.-])[\w][\w.-]*\.(?:${EXT})(?![\w])`, "gi");
/** Most pictures shown under one message. */
const MAX = 8;

/** The picture files a message names, in order: absolute paths, and bare
 *  names that follow one ("a.png, b.png" after "C:/tmp/a.png" are in C:/tmp).
 *  Web addresses aren't files. Pure. */
export function findImagePaths(text: string): string[] {
  const src = text.replace(/\b[a-z][a-z0-9+.-]*:\/\/\S+/gi, (m) => " ".repeat(m.length));
  const found: { at: number; path: string }[] = [];
  const taken: [number, number][] = [];
  for (const m of src.matchAll(ABS)) {
    found.push({ at: m.index!, path: m[0] });
    taken.push([m.index!, m.index! + m[0].length]);
  }
  for (const m of src.matchAll(BARE)) {
    const at = m.index!;
    if (taken.some(([a, b]) => at >= a && at < b)) continue;
    // The folder of the nearest absolute path before it.
    const prev = found.filter((f) => f.at < at && /[\\/]/.test(f.path)).sort((a, b) => b.at - a.at)[0];
    if (!prev) continue;
    const dir = prev.path.slice(0, Math.max(prev.path.lastIndexOf("/"), prev.path.lastIndexOf("\\")) + 1);
    found.push({ at, path: dir + m[0] });
  }
  const out: string[] = [];
  for (const f of found.sort((a, b) => a.at - b.at)) if (!out.includes(f.path)) out.push(f.path);
  return out.slice(0, MAX);
}

// ---- fetching -------------------------------------------------------------

const cache = new Map<string, string | null>();          // path → data URL, null = can't
const waiting = new Map<string, ((url: string | null) => void)[]>();

function load(path: string, done: (url: string | null) => void) {
  if (cache.has(path)) { done(cache.get(path) ?? null); return; }
  const list = waiting.get(path);
  if (list) { list.push(done); return; }
  waiting.set(path, [done]);
  send({ type: "chat.image", path });
}

/** The host's answer (main.ts routes chat.image.data here). */
export function applyChatImage(msg: ChatImageDataMessage) {
  const url = msg.dataUrl || null;
  cache.set(msg.path, url);
  for (const done of waiting.get(msg.path) ?? []) done(url);
  waiting.delete(msg.path);
}

/** The strip under a message naming pictures, or null when it names none. */
export function imageStrip(text: string): HTMLElement | null {
  const paths = findImagePaths(text);
  if (!paths.length) return null;
  const strip = el("div", "pc-shots");
  for (const path of paths) {
    const cell = el("button", "pc-shot") as HTMLButtonElement;
    cell.type = "button";
    cell.title = path;
    cell.hidden = true;                                   // shown once it loads
    const img = document.createElement("img");
    img.alt = path.split(/[\\/]/).pop() ?? path;
    cell.appendChild(img);
    cell.addEventListener("click", (ev) => { ev.stopPropagation(); if (img.src) lightbox(img.src, path); });
    load(path, (url) => {
      if (!url) { cell.remove(); if (!strip.childElementCount) strip.remove(); return; }
      img.src = url;
      cell.hidden = false;
    });
    strip.appendChild(cell);
  }
  return strip;
}

function lightbox(src: string, path: string) {
  const box = el("div", "pc-lightbox");
  box.setAttribute("role", "dialog");
  box.setAttribute("aria-label", path);
  const img = document.createElement("img");
  img.src = src;
  img.alt = path;
  box.append(img, el("div", "pc-lightbox__cap", path));
  const close = () => { box.remove(); document.removeEventListener("keydown", onKey, true); };
  const onKey = (ev: KeyboardEvent) => { if (ev.key === "Escape") { ev.stopPropagation(); ev.preventDefault(); close(); } };
  box.addEventListener("click", close);
  document.addEventListener("keydown", onKey, true);
  document.body.appendChild(box);
}
