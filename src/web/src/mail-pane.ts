// MailPane = a leaf pane that shows one email thread from the inbox, the left
// half of an email's session (a Claude sits on the right).
//
// Like BoardPane it owns no data: the host keeps the thread (a local copy of
// what the Gmail export wrote to Drive) and answers inbox.mail.request with
// the parsed messages. The thread's state (read / pending / done) is shown
// and set here too, because this is where you are when you decide it.

import { send } from "./bridge.js";
import type { PaneTreeView, InboxMailMessage, InboxItemView, InboxStateName } from "./bridge.js";
import { buildPaneHeader, applyChips } from "./pane-header.js";

const STATE_ACTIONS: { state: InboxStateName; label: string }[] = [
  { state: "read", label: "Read" },
  { state: "pending", label: "Pending" },
  { state: "done", label: "Done" },
];

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

/** "Tue 23 Sep, 14:05" — the reader's local time. */
export function mailDate(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "";
  return d.toLocaleString(undefined, { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

/** Split a reply into what's new and the quoted history under it: from the
 *  first "> " line or the "On <date>, <someone> wrote:" line that introduces
 *  it (which Gmail wraps onto two lines when it's long). */
export function splitQuoted(body: string): { fresh: string; quoted: string } {
  const lines = body.split("\n");
  let cut = -1;
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i].trim();
    if (l.startsWith(">")) { cut = i; break; }
    if (/wrote:$/.test(l)) {
      cut = /^On /.test(l) ? i : i > 0 && /^On /.test(lines[i - 1].trim()) ? i - 1 : i;
      break;
    }
  }
  // Nothing quoted, or nothing BUT quote: show it all rather than an empty card.
  if (cut <= 0) return { fresh: body.trimEnd(), quoted: "" };
  return { fresh: lines.slice(0, cut).join("\n").trimEnd(), quoted: lines.slice(cut).join("\n").trim() };
}

/** First words of the new part of a message, for its folded line. */
function peekOf(body: string): string {
  return splitQuoted(body).fresh.replace(/\[image:[^\]]*\]/g, "").replace(/\s+/g, " ").trim().slice(0, 140);
}

export class MailPane {
  readonly paneId: string;
  readonly threadId: string;
  readonly element: HTMLElement;
  private readonly nameEl: HTMLElement;
  private readonly stateDotEl: HTMLElement;
  private readonly colorDotEl: HTMLElement;
  private readonly branchEl: HTMLElement;
  private readonly commitsEl: HTMLElement;
  private readonly surface: HTMLElement;
  /** Latest-message time of the copy on screen; a newer one in an inbox
   *  push means the thread was re-exported with new mail, so re-request. */
  private shownDate = "";
  private readonly stateButtons = new Map<InboxStateName, HTMLButtonElement>();
  private readonly images = new Map<string, HTMLImageElement>();

  constructor(paneId: string, name: string, threadId: string) {
    this.paneId = paneId;
    this.threadId = threadId;

    this.element = el("div", "pane pane--mail");
    this.element.dataset.paneId = paneId;

    const header = buildPaneHeader(paneId);
    this.element.appendChild(header.root);
    this.nameEl = header.nameEl;
    this.stateDotEl = header.stateDotEl;
    this.colorDotEl = header.colorDotEl;
    this.branchEl = header.branchEl;
    this.commitsEl = header.commitsEl;
    this.nameEl.textContent = name;

    const slot = el("div", "pane__mailslot");
    this.surface = el("div", "mail");
    slot.appendChild(this.surface);
    this.element.appendChild(slot);
    this.surface.appendChild(el("div", "mail__empty", "Loading the email…"));

    this.element.addEventListener("mousedown", () => send({ type: "pane.focus", paneId: this.paneId }));
  }

  attach(host: HTMLElement) {
    host.appendChild(this.element);
    send({ type: "inbox.mail.request", paneId: this.paneId, id: this.threadId });
  }

  dispose() { this.element.remove(); }
  setName(name: string) { this.nameEl.textContent = name; }
  setActive(active: boolean) { this.element.classList.toggle("pane--active", active); }
  focus() { /* read-only surface */ }
  feed(_b64: string) { /* no terminal to feed */ }
  notifyExit(_code: number) { /* nothing to exit */ }
  forceRefit() { /* flows like a document; nothing to re-measure */ }
  changeFontSize(): number { return 0; }
  resetFontSize(): number { return 0; }

  applyLeafView(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    this.nameEl.textContent = leaf.name;
    this.stateDotEl.dataset.state = leaf.agentState;
    this.colorDotEl.dataset.color = String(leaf.colorIndex);
    this.element.dataset.color = String(leaf.colorIndex);
    applyChips(this.branchEl, this.commitsEl, leaf, false);
  }

  /** This thread's row from the latest inbox push, if it's in the list. */
  applyInboxItem(item: InboxItemView) {
    this.setState(item.state);
    if (this.shownDate && item.date !== this.shownDate)
      send({ type: "inbox.mail.request", paneId: this.paneId, id: this.threadId });
  }

  applyImage(name: string, dataUrl: string) {
    const img = this.images.get(name);
    if (img) img.src = dataUrl;
  }

  private setState(state: InboxStateName) {
    for (const [s, b] of this.stateButtons) b.setAttribute("aria-pressed", String(s === state));
  }

  applyMail(msg: InboxMailMessage) {
    this.images.clear();
    this.stateButtons.clear();
    const frag = document.createDocumentFragment();

    if (!msg.found) {
      frag.appendChild(el("div", "mail__empty",
        "This email isn't on this PC yet. It is copied down when the inbox syncs — open the inbox and press Refresh."));
      this.surface.replaceChildren(frag);
      return;
    }

    const head = el("div", "mail__head");
    head.appendChild(el("div", "mail__subject", msg.subject || "(no subject)"));
    const actions = el("div", "mail__states");
    actions.setAttribute("role", "group");
    actions.setAttribute("aria-label", "Email state");
    for (const a of STATE_ACTIONS) {
      const b = el("button", "mail__state", a.label) as HTMLButtonElement;
      b.type = "button";
      b.addEventListener("click", () => {
        this.setState(a.state);
        send({ type: "inbox.setState", id: this.threadId, state: a.state });
      });
      actions.appendChild(b);
      this.stateButtons.set(a.state, b);
    }
    head.appendChild(actions);
    frag.appendChild(head);

    const list = el("div", "mail__messages");
    const lastIdx = msg.messages.length - 1;
    msg.messages.forEach((m, i) => list.appendChild(this.buildMessage(m, i < lastIdx)));
    frag.appendChild(list);

    this.surface.replaceChildren(frag);
    this.setState(msg.state);
    this.shownDate = msg.messages.length ? msg.messages[lastIdx].date : "";
    // Older messages are folded, so the newest one sits right under the
    // header; start at the top.
    this.surface.scrollTop = 0;
  }

  /** One message card. `folded` = an earlier message in the thread: one line
   *  (sender, first words, date) that opens on click, like a mail client. */
  private buildMessage(m: InboxMailMessage["messages"][number], folded: boolean): HTMLElement {
    const card = el("article", "mail__msg");
    card.classList.toggle("mail__msg--folded", folded);

    const meta = el("div", "mail__meta");
    meta.appendChild(el("span", "mail__from", m.from));
    meta.appendChild(el("span", "mail__date", mailDate(m.date)));
    meta.addEventListener("click", () => card.classList.toggle("mail__msg--folded"));
    card.appendChild(meta);
    card.appendChild(el("div", "mail__peek", peekOf(m.body)));

    const full = el("div", "mail__full");
    const to = [m.to && `To: ${m.to}`, m.cc && `Cc: ${m.cc}`].filter(Boolean).join("  ·  ");
    if (to) full.appendChild(el("div", "mail__to", to));

    // Pasted screenshots: Gmail's text body marks each as "[image: x.png]",
    // and the export saved them as this message's image attachments in the
    // same order, so the n-th marker is the n-th image.
    const images = m.attachments.filter((a) => a.isImage && a.present);
    let nextImage = 0;
    const renderText = (text: string, host: HTMLElement) => {
      const rx = /\[image:[^\]]*\]/g;
      let at = 0;
      for (let hit = rx.exec(text); hit; hit = rx.exec(text)) {
        host.append(text.slice(at, hit.index));
        at = hit.index + hit[0].length;
        const a = images[nextImage++];
        if (a) host.appendChild(this.image(a.name));
      }
      host.append(text.slice(at));
    };

    const { fresh, quoted } = splitQuoted(m.body);
    const body = el("div", "mail__body");
    renderText(fresh, body);
    full.appendChild(body);
    if (quoted) {
      const toggle = el("button", "mail__quote-toggle", "···") as HTMLButtonElement;
      toggle.type = "button";
      toggle.title = "Show quoted text";
      const q = el("div", "mail__body mail__quoted");
      q.hidden = true;
      renderText(quoted, q);
      toggle.addEventListener("click", () => {
        q.hidden = !q.hidden;
        toggle.title = q.hidden ? "Show quoted text" : "Hide quoted text";
      });
      full.append(toggle, q);
    }

    // Whatever wasn't placed inline: files, and images with no marker.
    const rest = m.attachments.filter((a) => !(a.isImage && a.present) || images.indexOf(a) >= nextImage);
    if (rest.length) {
      const atts = el("div", "mail__atts");
      for (const a of rest) {
        if (a.isImage && a.present) { atts.appendChild(this.image(a.name)); continue; }
        const chip = el("button", "mail__att", a.name) as HTMLButtonElement;
        chip.type = "button";
        chip.disabled = !a.present;
        chip.title = a.present ? "Open with the default app" : "Too large to copy down, or not synced yet";
        chip.addEventListener("click", () => this.openAttachment(a.name));
        atts.appendChild(chip);
      }
      full.appendChild(atts);
    }
    card.appendChild(full);
    return card;
  }

  private image(name: string): HTMLImageElement {
    const img = document.createElement("img");
    img.className = "mail__img";
    img.alt = name;
    img.title = `${name} — click to open`;
    img.addEventListener("click", () => this.openAttachment(name));
    this.images.set(name, img);
    send({ type: "inbox.image.request", paneId: this.paneId, id: this.threadId, name });
    return img;
  }

  private openAttachment(name: string) {
    send({ type: "inbox.attachment.open", paneId: this.paneId, id: this.threadId, name });
  }
}
