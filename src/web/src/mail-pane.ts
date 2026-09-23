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
    for (const m of msg.messages) {
      const card = el("article", "mail__msg");
      const meta = el("div", "mail__meta");
      meta.appendChild(el("span", "mail__from", m.from));
      meta.appendChild(el("span", "mail__date", mailDate(m.date)));
      card.appendChild(meta);
      const to = [m.to && `To: ${m.to}`, m.cc && `Cc: ${m.cc}`].filter(Boolean).join("  ·  ");
      if (to) card.appendChild(el("div", "mail__to", to));
      card.appendChild(el("div", "mail__body", m.body));

      if (m.attachments.length) {
        const atts = el("div", "mail__atts");
        for (const a of m.attachments) {
          if (a.isImage && a.present) {
            const img = document.createElement("img");
            img.className = "mail__img";
            img.alt = a.name;
            img.title = `${a.name} — click to open`;
            img.addEventListener("click", () => this.openAttachment(a.name));
            this.images.set(a.name, img);
            atts.appendChild(img);
            send({ type: "inbox.image.request", paneId: this.paneId, id: this.threadId, name: a.name });
          } else {
            const chip = el("button", "mail__att", a.name) as HTMLButtonElement;
            chip.type = "button";
            chip.disabled = !a.present;
            chip.title = a.present ? "Open with the default app" : "Too large to copy down, or not synced yet";
            chip.addEventListener("click", () => this.openAttachment(a.name));
            atts.appendChild(chip);
          }
        }
        card.appendChild(atts);
      }
      list.appendChild(card);
    }
    frag.appendChild(list);

    this.surface.replaceChildren(frag);
    this.setState(msg.state);
    this.shownDate = msg.messages.length ? msg.messages[msg.messages.length - 1].date : "";
    // Newest message last, like any mail client's thread view — land on it.
    requestAnimationFrame(() => {
      const last = list.lastElementChild as HTMLElement | null;
      if (last && msg.messages.length > 1) this.surface.scrollTop = last.offsetTop - 8;
    });
  }

  private openAttachment(name: string) {
    send({ type: "inbox.attachment.open", paneId: this.paneId, id: this.threadId, name });
  }
}
