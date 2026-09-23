// Inbox = the full-window list of emails you've labelled `claude` in Gmail
// (exported to Drive, synced by the host's InboxController). Same overlay
// family as the dashboard: opened from the sidebar's Inbox button, Esc or ✕
// closes it.
//
// A row's click is its main verb: go to the email's session, making one
// (email on the left, a Claude on the right) if there isn't one yet. The
// state buttons on the row set read / pending / done without leaving the list.

import { send } from "./bridge.js";
import type { InboxStateMessage, InboxItemView, InboxStateName } from "./bridge.js";
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
    this.render();
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
      pill(`${msg.counts.new} new`, msg.counts.new ? "work" : "muted");
      pill(`${msg.counts.pending} pending`, msg.counts.pending ? "alert" : "muted");
      head.appendChild(counts);
    }
    const tail = el("div", "inbox__headtail");
    if (msg?.enabled) {
      const sync = el("span", "inbox__sync");
      if (msg.status === "syncing") sync.textContent = "Syncing…";
      else if (msg.status === "error") { sync.textContent = "Sync failed"; sync.classList.add("inbox__sync--error"); }
      else if (msg.lastSync) { sync.append("Synced "); sync.appendChild(agoSpan(Date.parse(msg.lastSync))); }
      tail.appendChild(sync);
      const refresh = el("button", "settings-btn settings-btn--subtle", "Refresh") as HTMLButtonElement;
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

    if (msg.message) frag.appendChild(el("div", `inbox__note${msg.status === "error" ? " inbox__note--error" : ""}`, msg.message));

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
    if (!items.length) {
      frag.appendChild(el("div", "inbox__empty",
        msg.items.length
          ? "Nothing in this view."
          : "No emails yet. Label an email “claude” in Gmail and it shows up here within about 5 minutes."));
    } else {
      const list = el("div", "inbox__list");
      for (const item of items) list.appendChild(this.row(item));
      frag.appendChild(list);
    }
    this.root.replaceChildren(frag);
  }

  private row(item: InboxItemView): HTMLElement {
    const row = el("div", "inbox__row");
    row.dataset.state = item.state;
    row.tabIndex = 0;
    row.title = item.sessionId ? "Go to this email's session" : "Open a session for this email";
    const open = () => {
      closeTeamRoom();
      send({ type: "inbox.open", id: item.id });
      this.hide();
    };
    row.addEventListener("click", open);
    row.addEventListener("keydown", (ev) => { if (ev.key === "Enter") open(); });

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
    if (item.sessionId) meta.appendChild(el("span", "chip inbox__chip-session", "Session"));
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
