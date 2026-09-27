// Pictures pasted into a project chat's box or a thread's. The page can't
// read the clipboard's bitmap or write files, so a paste asks the host
// (chat.paste), which saves the PNG in the chat's folder and answers with its
// path and a preview. The preview sits above the box until you send; the
// message then names each file, and Claude opens it.

import { send } from "./bridge.js";
import type { ChatPasteDataMessage } from "./bridge.js";
import { showToast } from "./toast.js";
import { el, button, icon } from "./thread-ui.js";

/** Whether a paste carries a picture and no text (a screenshot), which the
 *  box can't take as typing. Copying from a document gives both; that pastes
 *  as the text. */
export function pastesImage(types: readonly string[], hasText: boolean): boolean {
  return !hasText && types.some((t) => t.startsWith("image/"));
}

/** The message as sent: what you wrote, then each picture on one line —
 *  one line, because a thread's messages are typed into its terminal, where
 *  a newline would send early. Pure. */
export function withImages(text: string, paths: readonly string[]): string {
  if (!paths.length) return text;
  const refs = paths.map((p) => `[Image: ${p}]`).join(" ");
  const ask = paths.length === 1 ? "open the image to see it" : "open the images to see them";
  return `${text.trim() ? text.trim() + " " : ""}${refs} (${ask}).`;
}

/** The pictures a message named, and its words without them — for drawing
 *  your sent message with chips instead of paths. Pure. */
export function splitImages(text: string): { text: string; images: string[] } {
  const images: string[] = [];
  const rest = text
    .replace(/\[Image: ([^\]]+)\]/g, (_m, p: string) => { images.push(p); return ""; })
    .replace(/\s*\(open the images? to see (?:it|them)\)\.?\s*$/, "")
    .replace(/[ \t]{2,}/g, " ").trim();
  return { text: rest, images };
}

/** Your message as a bubble: its words, and a chip per picture it sent. */
export function userBubble(text: string): HTMLElement {
  const { text: words, images } = splitImages(text);
  const bubble = el("div", "pc-bubble", words);
  if (images.length) {
    const chips = el("div", "pc-bubble__images");
    for (const p of images) {
      const chip = el("span", "pc-bubble__image");
      chip.title = p;
      chip.append(icon("image"), el("span", undefined, "Picture"));
      chips.appendChild(chip);
    }
    bubble.appendChild(chips);
  }
  return bubble;
}

export class ImageTray {
  readonly element: HTMLElement;
  private items: { path: string; node: HTMLElement }[] = [];

  constructor(private readonly sessionId: () => string, private readonly changed: () => void = () => {}) {
    this.element = el("div", "pc-attach");
    this.element.hidden = true;
  }

  /** Wire a box: a picture pasted into it goes to the host. */
  listen(box: HTMLTextAreaElement) {
    box.addEventListener("paste", (ev) => {
      const dt = ev.clipboardData;
      if (!dt) return;
      const types = [...dt.items].map((i) => i.type);
      if (!pastesImage(types, (dt.getData("text/plain") ?? "") !== "")) return;
      ev.preventDefault();
      const id = this.sessionId();
      if (id) send({ type: "chat.paste", sessionId: id });
    });
  }

  /** The host's answer, if it was for this box's tab. */
  apply(msg: ChatPasteDataMessage) {
    if (msg.sessionId !== this.sessionId()) return;
    if (msg.error || !msg.path) { showToast(msg.error || "Couldn't paste that picture.", "warn"); return; }
    const path = msg.path;
    const node = el("div", "pc-attach__item");
    const img = document.createElement("img");
    img.alt = "Pasted picture";
    if (msg.dataUrl) img.src = msg.dataUrl;
    node.append(img, button("pc-attach__remove", icon("close"), () => this.remove(path), "Remove this picture"));
    this.items.push({ path, node });
    this.element.appendChild(node);
    this.element.hidden = false;
    this.changed();
  }

  paths(): string[] { return this.items.map((i) => i.path); }

  clear() {
    this.items = [];
    this.element.replaceChildren();
    this.element.hidden = true;
    this.changed();
  }

  private remove(path: string) {
    const i = this.items.findIndex((x) => x.path === path);
    if (i < 0) return;
    this.items[i].node.remove();
    this.items.splice(i, 1);
    this.element.hidden = this.items.length === 0;
    this.changed();
  }
}
