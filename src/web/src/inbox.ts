// Inbox = the full-window list of emails you've labelled `claude` in Gmail
// (exported to Drive, synced by the host's InboxController). Same overlay
// family as the dashboard: opened from the sidebar's Inbox button, Esc or ✕
// closes it.
//
// Clicking a row READS the email, in a panel beside the list; starting a
// session for it (email on the left, a Claude on the right) is a separate,
// deliberate button in that panel. The row's hover buttons set read /
// pending / done without opening anything.

import { send } from "./bridge.js";
import type { InboxStateMessage, InboxItemView, InboxStateName, InboxMailMessage } from "./bridge.js";
import { MailView } from "./mail-pane.js";

/** Address of the reading panel in inbox.mail / inbox.image replies — not a
 *  pane, so the empty id. */
export const READER_ID = "00000000-0000-0000-0000-000000000000";
import { closeTeamRoom } from "./team-room.js";
import { openSettings } from "./settings.js";
import { agoSpan } from "./elapsed.js";

type Filter = "open" | "pending" | "done" | "all";

const FILTERS: { id: Filter; label: string }[] = [
  { id: "open", label: "Open" },
  { id: "pending", label: "Pending" },
  { id: "done", label: "Done" },
  { id: "all", label: "All" },
];

const STATE_LABEL: Record<InboxStateName, string> = {
  new: "New", read: "Read", pending: "Pending", done: "Done",
};

const el = (tag: string, cls?: string, text?: string): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
};

function matches(f: Filter, s: InboxStateName): boolean {
  if (f === "all") return true;
  if (f === "open") return s !== "done";
  return s === f;
}

/** "14:05" today, "Mon" this week, else "12 Sep". */
function shortDate(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return "";
  const now = new Date();
  if (d.toDateString() === now.toDateString())
    return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  if (now.getTime() - d.getTime() < 6 * 86400000)
    return d.toLocaleDateString(undefined, { weekday: "short" });
  return d.toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

export class Inbox {
  private readonly root: HTMLElement;
  private readonly button: HTMLElement;
  private readonly badge: HTMLElement;
  private last: InboxStateMessage | null = null;
  private filter: Filter = "open";
  /** The email being read, if any. The reader outlives re-renders (its DOM
   *  is moved, not rebuilt) so a list refresh doesn't reload the email. */
  private selected: string | null = null;
  private readonly reader = new MailView(READER_ID, "");
  /** Tabs that exist right now, from the latest state push. The host's
   *  thread → tab link outlives a closed tab; this is what says whether
   *  "Go to session" still has somewhere to go. */
  private liveSessions = new Set<string>();

  setLiveSessions(ids: string[]) {
    const next = new Set(ids);
    const changed = next.size !== this.liveSessions.size || ids.some((id) => !this.liveSessions.has(id));
    this.liveSessions = next;
    if (changed && this.isOpen()) {
      this.render();
      if (this.selected) this.reader.request();   // the header's button label
    }
  }

  private hasSession(item: InboxItemView | undefined): boolean {
    return !!item?.sessionId && this.liveSessions.has(item.sessionId);
  }

  constructor(root: HTMLElement, button: HTMLElement, badge: HTMLElement) {
    this.root = root;
    this.button = button;
    this.badge = badge;
  }

  isOpen(): boolean { return document.body.classList.contains("show-inbox"); }

  show() {
    document.body.classList.add("show-inbox");
    this.root.setAttribute("aria-hidden", "false");
    this.render();
    // Opening the list is the moment you want it current.
    if (this.last?.enabled && this.last.status !== "syncing") send({ type: "inbox.refresh" });
  }

  hide() {
    document.body.classList.remove("show-inbox");
    this.root.setAttribute("aria-hidden", "true");
  }

  toggle() { this.isOpen() ? this.hide() : this.show(); }

  apply(msg: InboxStateMessage) {
    this.last = msg;
    this.button.hidden = !msg.enabled;
    const n = msg.counts.new ?? 0;
    this.badge.textContent = String(n);
    this.badge.style.display = n > 0 ? "" : "none";
    if (!msg.enabled && this.isOpen()) this.hide();
    const item = msg.items.find((i) => i.id === this.selected);
    if (item) this.reader.applyInboxItem(item);
    this.render();
  }

  applyMail(msg: InboxMailMessage) { this.reader.applyMail(msg); }
  applyImage(name: string, dataUrl: string) { this.reader.applyImage(name, dataUrl); }

  private select(id: string) {
    if (this.selected === id) return;
    this.selected = id;
    this.reader.threadId = id;
    this.reader.surface.replaceChildren(el("div", "mail__empty", "Loading the email…"));
    this.reader.headerExtras = () => this.sessionButton(id);
    // The host marks it read and answers with inbox.mail for READER_ID.
    send({ type: "inbox.view", id });
    this.render();
  }

  /** The panel's one call to action: start working on this email with Claude,
   *  or go back to the session already started for it. */
  private sessionButton(id: string): HTMLElement {
    const item = this.last?.items.find((i) => i.id === id);
    const b = el("button", "settings-btn settings-btn--accent mail__session",
      this.hasSession(item) ? "Go to session" : "Open session") as HTMLButtonElement;
    b.type = "button";
    b.addEventListener("click", () => {
      closeTeamRoom();
      send({ type: "inbox.open", id });
      this.hide();
    });
    return b;
  }

  private render() {
    if (!this.isOpen()) return;
    const msg = this.last;
    const frag = document.createDocumentFragment();

    const head = el("div", "dash__head");
    head.appendChild(el("div", "dash__title", "Inbox"));
    if (msg?.enabled) {
      const counts = el("div", "dash__counts");
      const pill = (text: string, v: string) => counts.appendChild(el("span", `dash__count dash__count--${v}`, text));
      if (msg.counts.new) pill(`${msg.counts.new} new`, "work");
      if (msg.counts.pending) pill(`${msg.counts.pending} pending`, "alert");
      head.appendChild(counts);
    }
    const tail = el("div", "inbox__headtail");
    if (msg?.enabled) {
      const sync = el("span", "inbox__sync");
      if (msg.status === "syncing") sync.textContent = "Syncing…";
      else if (msg.status === "error") { sync.textContent = "Sync failed"; sync.classList.add("inbox__sync--error"); }
      else if (msg.lastSync) { sync.append("Synced "); sync.appendChild(agoSpan(Date.parse(msg.lastSync))); }
      tail.appendChild(sync);
      const refresh = el("button", "inbox__refresh", "Refresh") as HTMLButtonElement;
      refresh.type = "button";
      refresh.disabled = msg.status === "syncing";
      refresh.addEventListener("click", () => send({ type: "inbox.refresh" }));
      tail.appendChild(refresh);
    }
    const close = el("button", "dash__close");
    close.setAttribute("aria-label", "Close (Esc)");
    close.innerHTML =
      '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4"><path d="M4 4l8 8M12 4l-8 8" stroke-linecap="round"/></svg>';
    close.addEventListener("click", () => this.hide());
    tail.appendChild(close);
    head.appendChild(tail);
    frag.appendChild(head);

    if (!msg?.enabled) {
      const empty = el("div", "inbox__empty");
      empty.appendChild(el("p", undefined, "The inbox is off. Turn it on in Settings → Inbox."));
      const b = el("button", "settings-btn settings-btn--subtle", "Open settings") as HTMLButtonElement;
      b.type = "button";
      b.addEventListener("click", () => { this.hide(); openSettings(); });
      empty.appendChild(b);
      frag.appendChild(empty);
      this.root.replaceChildren(frag);
      return;
    }

    if (msg.message) {
      const note = el("div", `inbox__note${msg.status === "error" ? " inbox__note--error" : ""}`);
      note.appendChild(el("span", "inbox__note-text", msg.message));
      // States aren't shared yet because the folder has no state file: offer
      // to make it rather than send you off to Drive.
      if (msg.status === "ok" && !msg.shared) {
        const make = el("button", "inbox__refresh", "Create state file") as HTMLButtonElement;
        make.type = "button";
        make.addEventListener("click", () => { make.disabled = true; send({ type: "inbox.createStateFile" }); });
        note.appendChild(make);
      }
      frag.appendChild(note);
    }

    const tabs = el("div", "inbox__filters");
    tabs.setAttribute("role", "tablist");
    for (const f of FILTERS) {
      const count = msg.items.filter((i) => matches(f.id, i.state)).length;
      const t = el("button", "inbox__filter") as HTMLButtonElement;
      t.type = "button";
      t.setAttribute("role", "tab");
      t.setAttribute("aria-selected", String(f.id === this.filter));
      t.append(f.label);
      t.appendChild(el("span", "inbox__filter-count", String(count)));
      t.addEventListener("click", () => { this.filter = f.id; this.render(); });
      tabs.appendChild(t);
    }
    frag.appendChild(tabs);

    const items = msg.items.filter((i) => matches(this.filter, i.state));
    if (this.selected && !msg.items.some((i) => i.id === this.selected)) this.selected = null;
    this.root.classList.toggle("inbox--reading", this.selected !== null);
    const body = el("div", "inbox__body");
    if (!items.length) {
      body.appendChild(el("div", "inbox__empty",
        msg.items.length
          ? "Nothing in this view."
          : "No emails yet. Label an email “claude” in Gmail and it shows up here within about 5 minutes."));
    } else {
      const list = el("div", "inbox__list");
      for (const item of items) list.appendChild(this.row(item));
      body.appendChild(list);
    }
    if (this.selected) {
      const panel = el("div", "inbox__reader");
      panel.appendChild(this.reader.surface);
      body.appendChild(panel);
    }
    frag.appendChild(body);
    this.root.replaceChildren(frag);
  }

  private row(item: InboxItemView): HTMLElement {
    const row = el("div", "inbox__row");
    row.dataset.state = item.state;
    row.tabIndex = 0;
    row.setAttribute("aria-selected", String(item.id === this.selected));
    row.addEventListener("click", () => this.select(item.id));
    row.addEventListener("keydown", (ev) => { if (ev.key === "Enter") this.select(item.id); });

    row.appendChild(el("span", "inbox__dot"));
    const main = el("div", "inbox__main");
    const line1 = el("div", "inbox__line1");
    line1.appendChild(el("span", "inbox__from", item.from || "(unknown sender)"));
    if (item.messageCount > 1) line1.appendChild(el("span", "inbox__n", String(item.messageCount)));
    line1.appendChild(el("span", "inbox__subject", item.subject));
    main.appendChild(line1);
    if (item.snippet) main.appendChild(el("div", "inbox__snippet", item.snippet));
    row.appendChild(main);

    const side = el("div", "inbox__side");
    const meta = el("div", "inbox__meta");
    if (item.attachmentCount) meta.appendChild(el("span", "chip", `${item.attachmentCount} file${item.attachmentCount === 1 ? "" : "s"}`));
    if (this.hasSession(item)) meta.appendChild(el("span", "chip inbox__chip-session", "Session"));
    meta.appendChild(el("span", `inbox__state inbox__state--${item.state}`, STATE_LABEL[item.state]));
    meta.appendChild(el("span", "inbox__date", shortDate(item.date)));
    side.appendChild(meta);

    const actions = el("div", "inbox__actions");
    const act = (label: string, state: InboxStateName) => {
      const b = el("button", "inbox__act", label) as HTMLButtonElement;
      b.type = "button";
      b.addEventListener("click", (ev) => {
        ev.stopPropagation();
        send({ type: "inbox.setState", id: item.id, state });
      });
      actions.appendChild(b);
    };
    if (item.state === "new") act("Mark read", "read");
    if (item.state !== "pending" && item.state !== "done") act("Pending", "pending");
    if (item.state === "done") act("Reopen", "read");
    else act("Done", "done");
    side.appendChild(actions);
    row.appendChild(side);
    return row;
  }
}
