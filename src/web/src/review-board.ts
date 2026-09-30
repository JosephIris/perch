// A project chat's board, drawn in the Overview when the chat has one
// (`perch thread board` fills it). Two views in one panel:
//
//   * the columns: Checking · Needs you · Ready to approve · Done, one card
//     per item — its key, short title, verdict chip, what it would move to,
//     and the question it has for you, if any. Cards move between columns on
//     their own as threads report and you decide; nothing is dragged, since
//     moving a card to Done would mean posting something.
//   * one card opened: its result and draft on the left with Approve / Edit /
//     Skip, and its thread on the right (the Overview puts the thread there).
//
// The buttons don't act: they send the chat a line in your name ("approve
// PK-1", "skip PK-1", "PK-1: <answer>"), exactly as if you had typed it, and
// the chat and its thread do the rest and update the card.

import { send } from "./bridge.js";
import type { BoardView, BoardItemView, SessionView } from "./bridge.js";
import { renderMarkdown } from "./md.js";
import { el, button, icon, groupOf, reducedMotion } from "./thread-ui.js";

const EASE = "cubic-bezier(0, 0, 0, 1)";

export type BoardColumn = "checking" | "you" | "ready" | "done";

export const COLUMNS: { id: BoardColumn; label: string; empty: string }[] = [
  { id: "checking", label: "Checking", empty: "Nothing is being checked." },
  { id: "you", label: "Needs you", empty: "Nothing needs you." },
  { id: "ready", label: "Ready to approve", empty: "Nothing to approve yet." },
  { id: "done", label: "Done", empty: "Nothing decided yet." },
];

/** Where a card sits. A decision wins; then anything only you can settle;
 *  then whether it has a result yet. */
export function columnOf(it: Pick<BoardItemView, "done" | "question" | "tone">): BoardColumn {
  if (it.done.trim()) return "done";
  if (it.question.trim() || it.tone === "pending") return "you";
  if (it.tone === "checking") return "checking";
  return "ready";
}

/** The lines the buttons send the chat, in your name. */
export const lines = {
  approve: (key: string) => `approve ${key}`,
  approveWith: (key: string, comment: string) => `approve ${key} with this comment:\n\n${comment.trim()}`,
  skip: (key: string) => `skip ${key}`,
  answer: (key: string, text: string) => `${key}: ${text.trim()}`,
};

/** "Posted · In Progress", "Skipped": what a decided card says. */
export function doneLabel(it: Pick<BoardItemView, "done" | "note">): string {
  const d = it.done.trim();
  const word = d ? d[0].toUpperCase() + d.slice(1) : "";
  return it.note.trim() ? `${word} · ${it.note.trim()}` : word;
}

/** "1 verified · 1 not working · 6 manual check": the board in a line. */
export function tallyLine(items: BoardItemView[]): string {
  const order: BoardItemView["tone"][] = ["ok", "bad", "manual", "pending", "checking"];
  const parts: string[] = [];
  for (const tone of order) {
    const of = items.filter((i) => i.tone === tone);
    if (of.length) parts.push(`${of.length} ${of[0].label.toLowerCase()}`);
  }
  const done = items.filter((i) => i.done.trim()).length;
  return [`${items.length} item${items.length === 1 ? "" : "s"}`, ...parts, ...(done ? [`${done} done`] : [])].join(" · ");
}

/** What you sent from a card. The card stays locked, showing it, until the
 *  chat changes the card, you unlock it, or LOCK_MS passes, so one press is
 *  one message and a redraw can never offer it again. */
type Sent = { shown: string; at: number; updatedMs: number };
export const LOCK_MS = 2 * 60_000;
/** The same line again this soon is a double press, not a second message. */
export const REPEAT_MS = 10_000;

export class BoardPanel {
  readonly element: HTMLElement;
  private readonly boardView: HTMLElement;
  private readonly ticketView: HTMLElement;
  private readonly result: HTMLElement;
  /** Where the Overview puts the open card's thread. */
  readonly threadSlot: HTMLElement;
  private board: BoardView | null = null;
  private threads: SessionView[] = [];
  private boardSig = "";
  private ticketSig = "";
  private readonly sent = new Map<string, Sent>();
  /** The open card, or null on the columns. */
  key: string | null = null;
  // What you are typing on the open card survives a redraw.
  private editing = false;
  private editText = "";
  private answerText = "";
  /** A card was clicked. */
  onOpen: (key: string) => void = () => {};

  constructor(private readonly ask: (text: string) => void) {
    this.element = el("div", "rb");
    this.boardView = el("div", "rb__board");
    this.ticketView = el("div", "rb__ticket");
    this.ticketView.hidden = true;
    this.result = el("div", "rb__pane rb__result");
    this.threadSlot = el("div", "rb__pane rb__thread");
    this.ticketView.append(this.result, this.threadSlot);
    this.element.append(this.boardView, this.ticketView);
  }

  get hasCards(): boolean { return !!this.board?.items.length; }
  get title(): string { return this.board?.title || "Board"; }
  item(key: string): BoardItemView | undefined {
    return this.board?.items.find((i) => i.key.toLowerCase() === key.toLowerCase());
  }

  setBoard(board: BoardView | null | undefined) {
    this.board = board ?? null;
    // A card the chat has acted on (it changed) is no longer "sent".
    for (const [k, s] of this.sent) {
      const it = this.item(k);
      if (!it || it.updatedMs !== s.updatedMs || Date.now() - s.at > LOCK_MS) this.sent.delete(k);
    }
    this.render();
  }

  setThreads(threads: SessionView[]) {
    this.threads = threads;
    this.render();
  }

  showBoard() {
    this.key = null;
    this.editing = false;
    this.editText = this.answerText = "";
    this.ticketView.hidden = true;
    this.boardView.hidden = false;
    this.ticketSig = "";
    this.render();
    this.enter(this.boardView, -16);
  }

  showTicket(key: string) {
    if (this.key !== key) { this.editing = false; this.editText = this.answerText = ""; }
    this.key = key;
    this.boardView.hidden = true;
    this.ticketView.hidden = false;
    this.ticketSig = "";
    this.render();
    this.enter(this.ticketView, 16);
  }

  private enter(view: HTMLElement, dx: number) {
    if (reducedMotion() || !this.element.isConnected) return;
    view.animate([{ opacity: 0, transform: `translateX(${dx}px)` }, { opacity: 1, transform: "none" }], { duration: 200, easing: EASE });
  }

  private threadOf(it: BoardItemView): SessionView | undefined {
    return it.thread ? this.threads.find((t) => t.threadNumber === it.thread) : undefined;
  }

  private render() {
    if (this.key) this.renderTicket(); else this.renderBoard();
  }

  // ---- the columns ----------------------------------------------------------

  private renderBoard() {
    const b = this.board;
    const items = b?.items ?? [];
    const sig = JSON.stringify([b, [...this.sent.keys()], items.map((i) => { const t = this.threadOf(i); return t ? groupOf(t) : ""; })]);
    if (sig === this.boardSig) return;
    this.boardSig = sig;

    const head = el("div", "rb__head");
    // The live tally, not the chat's summary: a summary written once goes
    // stale as cards move, and it tends to repeat the counts.
    const meta = el("div", "rb__meta", items.length ? tallyLine(items) : "");
    if (b?.summary) meta.title = b.summary;
    head.append(el("div", "rb__title", b?.title || "Board"), meta);
    const cols = el("div", "rb__cols");
    for (const c of COLUMNS) {
      const of = items.filter((i) => columnOf(i) === c.id);
      // Once everything has reported, Checking only takes room.
      if (c.id === "checking" && !of.length) continue;
      const col = el("section", "rb__col");
      col.dataset.col = c.id;
      const h = el("div", "rb__col-head");
      if (c.id === "you" && of.length) h.appendChild(el("span", "rb__dot rb__dot--you"));
      h.append(el("span", undefined, c.label), el("span", "rb__col-n", String(of.length)));
      const list = el("div", "rb__col-list");
      if (!of.length) list.appendChild(el("div", "rb__col-empty", c.empty));
      for (const it of of) list.appendChild(this.card(it));
      col.append(h, list);
      cols.appendChild(col);
    }
    this.boardView.replaceChildren(head, cols);
  }

  private card(it: BoardItemView): HTMLElement {
    const card = button("rb-card", "", () => this.onOpen(it.key)) as HTMLElement;
    card.dataset.tone = it.tone;
    if (it.done.trim()) card.classList.add("rb-card--done");
    const top = el("div", "rb-card__top");
    top.appendChild(el("span", "rb-card__key", it.key));
    const t = this.threadOf(it);
    if (it.thread) {
      const n = el("span", "rb-card__thread", `#${it.thread}`);
      if (t) n.dataset.group = groupOf(t);
      top.appendChild(n);
    }
    card.append(top, el("div", "rb-card__title", it.title || it.key));
    if (it.finding && columnOf(it) !== "you") card.appendChild(el("div", "rb-card__find", it.finding));
    const foot = el("div", "rb-card__foot");
    foot.appendChild(chip(it));
    if (it.done.trim()) foot.appendChild(el("span", "rb-card__done", doneLabel(it)));
    else if (it.status) foot.appendChild(statusTo(it.status));
    card.appendChild(foot);
    if (it.question && !it.done.trim()) card.appendChild(el("div", "rb-card__ask", it.question));
    const s = this.sent.get(it.key);
    if (s) card.appendChild(el("div", "rb-card__sent", "Sent · waiting for the chat"));
    card.title = it.fullTitle || it.title;
    return card;
  }

  // ---- one card open ----------------------------------------------------------

  private renderTicket() {
    const it = this.key ? this.item(this.key) : undefined;
    const sig = JSON.stringify([it, this.sent.get(this.key ?? ""), this.editing]);
    if (sig === this.ticketSig) return;
    this.ticketSig = sig;
    if (!it) {
      this.result.replaceChildren(el("p", "ov__hint", "This card is no longer on the board."));
      return;
    }
    const decided = !!it.done.trim();
    const sent = this.sent.get(it.key);

    const head = el("div", "rb__pane-head");
    head.appendChild(el("span", undefined, "Result"));
    if (it.url) {
      const a = button("rb__link", "", () => send({ type: "url.open", url: it.url }), it.url);
      a.append(el("span", undefined, `Open ${it.key}`), icon("expand"));
      head.appendChild(a);
    }

    const body = el("div", "rb__pane-body");
    body.appendChild(el("h2", "rb__h", it.title || it.key));
    if (it.fullTitle && it.fullTitle !== it.title) body.appendChild(el("p", "rb__full", it.fullTitle));
    const row = el("div", "rb__row");
    row.appendChild(chip(it));
    if (decided) row.appendChild(el("span", "rb-card__done", doneLabel(it)));
    else if (it.status) { const s = statusTo(it.status); s.prepend("Proposed status "); row.appendChild(s); }
    body.appendChild(row);
    if (it.finding) body.appendChild(el("p", "rb__find", it.finding));

    if (sent && !decided) {
      // Locked: what went, and a way out if the chat never touches the card.
      const q = el("div", "rb__sent");
      const head = el("div", "rb__sent-head");
      head.append(el("span", undefined, "Sent to the chat · waiting for it"),
        button("rb__unlock", "Send something else", () => { this.sent.delete(it.key); this.boardSig = this.ticketSig = ""; this.render(); }));
      q.append(head, el("div", "rb__sent-text", sent.shown));
      body.appendChild(q);
    } else if (it.question && !decided) {
      const q = el("div", "rb__ask");
      q.appendChild(el("div", "rb__ask-text", it.question));
      const box = document.createElement("textarea");
      box.className = "pc-composer__input rb__ask-box";
      box.rows = 2;
      box.placeholder = "Your answer";
      box.setAttribute("aria-label", `Answer about ${it.key}`);
      box.value = this.answerText;
      box.addEventListener("input", () => { this.answerText = box.value; });
      box.addEventListener("keydown", (e) => {
        e.stopPropagation();
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); reply(); }
      });
      const reply = () => {
        const text = box.value.trim();
        if (!text) return;
        this.answerText = "";
        this.say(it, lines.answer(it.key, text), text);
      };
      const acts = el("div", "rb__ask-acts");
      acts.appendChild(button("pc-btn pc-btn--sm", "Send answer", reply));
      q.append(box, acts);
      body.appendChild(q);
    }

    let editBox: HTMLTextAreaElement | null = null;
    if (it.draft || this.editing) {
      const label = el("div", "rb__label");
      label.append(el("span", undefined, this.editing ? "Your comment" : "Draft"), el("span", "rb__path", basename(it.draftPath)));
      label.title = it.draftPath;
      body.appendChild(label);
      if (this.editing) {
        editBox = document.createElement("textarea");
        editBox.className = "settings-control settings-control--text settings-control--area rb__edit";
        editBox.value = this.editText || it.draft;
        editBox.rows = 14;
        editBox.setAttribute("aria-label", "Comment to post");
        editBox.addEventListener("input", () => { this.editText = editBox!.value; });
        editBox.addEventListener("keydown", (e) => e.stopPropagation());
        body.appendChild(editBox);
      } else {
        const d = el("div", "rb__draft md");
        d.appendChild(renderMarkdown(it.draft));
        body.appendChild(d);
      }
    }

    const acts = el("div", "rb__acts");
    if (decided) {
      acts.appendChild(el("span", "rb__hint", "Decided. Ask the chat if it needs to change."));
    } else if (this.editing) {
      acts.append(
        button("pc-btn pc-btn--primary", "Approve with this comment", () => {
          const text = editBox?.value.trim() ?? "";
          if (!text) return;
          this.editing = false;
          this.editText = "";
          this.say(it, lines.approveWith(it.key, text), `approve ${it.key} with your comment`);
        }),
        button("pc-btn pc-btn--quiet", "Cancel", () => { this.editing = false; this.editText = ""; this.ticketSig = ""; this.render(); }));
    } else {
      const approve = button("pc-btn pc-btn--primary", "Approve", () => this.say(it, lines.approve(it.key), lines.approve(it.key)));
      const edit = button("pc-btn", "Edit comment", () => { this.editing = true; this.ticketSig = ""; this.render(); });
      const skip = button("pc-btn pc-btn--quiet", "Skip", () => this.say(it, lines.skip(it.key), lines.skip(it.key)));
      edit.disabled = !it.draft || !!sent;
      approve.disabled = skip.disabled = !!sent;
      acts.append(approve, edit, skip);
      acts.appendChild(el("span", "rb__hint", sent
        ? "Waiting for the chat."
        : `Sends “approve ${it.key}” to the chat${it.status ? `, to post it and move it to ${it.status}` : ""}.`));
    }

    this.result.replaceChildren(head, body, acts);
    editBox?.focus();

    if (!it.thread) this.threadSlot.replaceChildren(el("p", "ov__hint rb__nothread", "No thread is linked to this card."));
  }

  private last: { line: string; at: number } | null = null;

  /** Send the chat a line in your name, once, and lock the card on it. */
  private say(it: BoardItemView, line: string, shown: string) {
    if (this.sent.has(it.key)) return;
    const now = Date.now();
    if (this.last && this.last.line === line && now - this.last.at < REPEAT_MS) return;
    this.last = { line, at: now };
    this.sent.set(it.key, { shown, at: now, updatedMs: it.updatedMs });
    this.ask(line);
    this.boardSig = this.ticketSig = "";
    this.render();
    // The lock lifts by itself if the chat never changes the card.
    window.setTimeout(() => {
      const s = this.sent.get(it.key);
      if (s && s.at === now) { this.sent.delete(it.key); this.boardSig = this.ticketSig = ""; this.render(); }
    }, LOCK_MS + 100);
  }
}

function chip(it: BoardItemView): HTMLElement {
  const c = el("span", "rb-chip", it.label);
  c.dataset.tone = it.tone;
  return c;
}

function statusTo(status: string): HTMLElement {
  const s = el("span", "rb-to");
  s.append("→ ", el("b", undefined, status));
  return s;
}

function basename(path: string): string {
  const m = path.match(/[^\\/]+$/);
  return m ? m[0] : path;
}
