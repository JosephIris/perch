// BoardPane = a leaf pane that renders its SESSION's board: the context
// staging surface you throw things at before handing a task to an agent.
//
// Three things about the shape of this file:
//
//  * It owns no data. The board lives on disk as `.perch/boards/<slug>/board.md`
//    and the host is its only writer; this pane renders whatever the last
//    `board.state` push said and sends intents back. That keeps one source of
//    truth for something an agent also reads.
//  * The board's PATH comes from the session, not from this leaf. Pane
//    adjacency is not stable in this tree (splits flatten, panes move, closing
//    a sibling reparents), so a board bound to "the pane next to me" would come
//    unstuck; see Session.BoardPath.
//  * A board that can't be read must SAY so. An empty dotted grid is
//    indistinguishable from a board you simply haven't filled in, which is the
//    same trap the URL pane fell into (see UrlPane.showError).

import { send } from "./bridge.js";
import type { PaneTreeView, BoardNodeView, BoardLinkView } from "./bridge.js";
import { buildPaneHeader, applyChips } from "./pane-header.js";

/** Icon path data per node kind, 24x24 viewBox, single stroke (Fluent). */
const KIND_ICON: Record<string, string> = {
  note:  "M5 6V4h14v2M12 4v16M9 20h6",
  path:  "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5",
  image: "M3 4h18v16H3z M9 10a1.5 1.5 0 100-3 1.5 1.5 0 000 3 M21 16l-5-5-7 7",
  url:   "M12 3a9 9 0 100 18 9 9 0 000-18z M3 12h18 M12 3a14 14 0 010 18 M12 3a14 14 0 000 18",
};

const KIND_LABEL: Record<string, string> = {
  note: "note", path: "file", image: "image", url: "reference",
};

/** Toolbar icons, same 24x24 single-stroke family as the card icons. */
const TOOL_ICON: Record<string, string> = {
  note:  "M5 4h14v11l-4 5H5z M15 20v-5h4 M9 9h6 M9 13h3",
  file:  KIND_ICON.path,
  link:  "M10.5 13.5a4 4 0 0 0 5.66 0l2.5-2.5a4 4 0 0 0-5.66-5.66l-1 1"
       + " M13.5 10.5a4 4 0 0 0-5.66 0l-2.5 2.5a4 4 0 0 0 5.66 5.66l1-1",
  paste: "M9 4h6v3H9z M9 5.5H6.5a1.5 1.5 0 0 0-1.5 1.5v11a1.5 1.5 0 0 0 1.5 1.5h11"
       + "a1.5 1.5 0 0 0 1.5-1.5V7a1.5 1.5 0 0 0-1.5-1.5H15",
  minus: "M5 12h14",
  plus:  "M12 5v14 M5 12h14",
};

/** How long an error banner stays up before fading. Long enough to read a
 *  sentence, short enough that it doesn't sit over a card you're using. */
const BANNER_MS = 7000;

/** Default card size per kind, used when a node has never been resized (w/h 0).
 *  Images get a taller box because they lead with a picture. */
const DEFAULT_SIZE: Record<string, { w: number; h: number }> = {
  note:  { w: 196, h: 116 },
  path:  { w: 196, h: 116 },
  url:   { w: 196, h: 132 },
  image: { w: 220, h: 208 },
};
const MIN_W = 120, MIN_H = 64, MAX_W = 1200, MAX_H = 1200;

/** Zoom range. Below ~40% a card's text stops being readable at all, and above
 *  ~250% a screenshot is bigger than the pane — both ends are where zooming
 *  stops being useful rather than arbitrary round numbers. */
const MIN_ZOOM = 0.4, MAX_ZOOM = 2.5;

function sizeOf(n: BoardNodeView): { w: number; h: number } {
  const def = DEFAULT_SIZE[n.kind] ?? DEFAULT_SIZE.note;
  return { w: n.w && n.w > 0 ? n.w : def.w, h: n.h && n.h > 0 ? n.h : def.h };
}

export class BoardPane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly nameEl: HTMLElement;
  private readonly stateDotEl: HTMLElement;
  private readonly stateLabelEl: HTMLElement;
  private readonly colorDotEl: HTMLElement;
  private readonly branchEl: HTMLElement;
  private readonly commitsEl: HTMLElement;
  private readonly surface: HTMLElement;
  /** Zoom layer holding the cards. See the constructor. */
  private readonly canvas: HTMLElement;
  /** Live zoom readout in the toolbar; also the reset-view button. */
  private readonly zoomPctEl: HTMLElement;
  /** Transient error strip. Kept as one element and re-used, like Toast. */
  private bannerEl: HTMLElement | null = null;
  private bannerTimer = 0;
  /** View scale. A VIEW preference, so it lives in memory and never reaches
   *  board.md — how far you happen to be zoomed in is not content, and writing
   *  it would make every zoom a file write and a git-visible change. */
  private zoom = 1;
  /** Pan offset in board space, moved to keep a zoom anchored under the
   *  pointer. */
  private panX = 0;
  private panY = 0;
  private boardPath = "";
  /** The open editor, if any: an existing node's text, or a card that doesn't
   *  exist yet (nodeId null). Held HERE rather than only in the DOM because a
   *  state push replaces every card — see applyBoardState — and losing what you
   *  were typing to somebody else's drag would be unforgivable. */
  private editor:
    | { nodeId: string | null; kind: "note" | "link"; text: string; x: number; y: number }
    | null = null;
  /** Last state pushed by the host. Read during a drag so the gesture works
   *  from the node's committed position rather than the DOM's. */
  private nodes: BoardNodeView[] = [];
  /** Previews already fetched, so a state push after every mutation doesn't
   *  re-encode every image on the board. */
  private readonly imageCache = new Map<string, string>();

  constructor(paneId: string, name: string, boardPath: string) {
    this.paneId = paneId;
    this.boardPath = boardPath;

    this.element = document.createElement("div");
    this.element.className = "pane pane--board";
    this.element.dataset.paneId = paneId;

    const header = buildPaneHeader(paneId);
    this.element.appendChild(header.root);
    this.nameEl       = header.nameEl;
    this.stateDotEl   = header.stateDotEl;
    this.stateLabelEl = header.stateLabelEl;
    this.colorDotEl   = header.colorDotEl;
    this.branchEl     = header.branchEl;
    this.commitsEl    = header.commitsEl;
    this.nameEl.textContent = name;

    const slot = document.createElement("div");
    slot.className = "pane__boardslot";
    this.element.appendChild(slot);

    this.surface = document.createElement("div");
    this.surface.className = "board";
    slot.appendChild(this.surface);

    // Cards live on an inner layer that carries the zoom transform, so zooming
    // is one CSS property on one element rather than arithmetic on every card.
    // Node coordinates stay in unscaled "board space" everywhere — the only
    // places that know about zoom are the gesture handlers, which divide
    // pointer deltas by it, and the wheel/keyboard handlers below.
    this.canvas = document.createElement("div");
    this.canvas.className = "board__canvas";
    this.surface.appendChild(this.canvas);

    // Tools sit ON the surface rather than in the pane header: the header is
    // shared with terminals and browser panes and says what the PANE is, while
    // these act on the board under them. Always visible, not hover-revealed —
    // the first version had no controls at all and "paste and hope" was the
    // entire discoverable surface.
    const tools = this.buildTools();
    this.zoomPctEl = tools.querySelector(".board__pct") as HTMLElement;
    this.surface.appendChild(tools);

    // Ctrl+wheel zooms about the pointer, the way every canvas app does it.
    // Plain wheel is left alone: there is nothing to scroll yet, and hijacking
    // it would be surprising.
    this.surface.addEventListener("wheel", (ev) => {
      if (!ev.ctrlKey) return;
      ev.preventDefault();
      const r = this.surface.getBoundingClientRect();
      this.setZoom(this.zoom * (ev.deltaY < 0 ? 1.1 : 1 / 1.1),
                   { x: ev.clientX - r.left, y: ev.clientY - r.top });
    }, { passive: false });

    // Drag empty space to pan. Standard for any canvas, and load-bearing here:
    // a card can sit outside a narrow pane's viewport (its position came from
    // the file, and clamping incoming positions would silently rewrite the
    // user's layout every time the pane got narrow), so panning is how you
    // reach it.
    this.surface.addEventListener("pointerdown", (ev) => {
      if (ev.button !== 0) return;
      // Cards own their own drag; the tools and the error strip own their clicks.
      if ((ev.target as HTMLElement).closest(".board-node, .board__tools, .board-banner")) return;
      const startX = ev.clientX, startY = ev.clientY;
      const p0x = this.panX, p0y = this.panY;
      this.surface.setPointerCapture(ev.pointerId);
      this.surface.classList.add("board--panning");

      const onMove = (m: PointerEvent) => {
        this.panX = p0x + (m.clientX - startX);
        this.panY = p0y + (m.clientY - startY);
        this.applyTransform();
      };
      const onUp = () => {
        this.surface.releasePointerCapture(ev.pointerId);
        this.surface.classList.remove("board--panning");
        this.surface.removeEventListener("pointermove", onMove);
        this.surface.removeEventListener("pointerup", onUp);
        this.surface.removeEventListener("pointercancel", onUp);
      };
      this.surface.addEventListener("pointermove", onMove);
      this.surface.addEventListener("pointerup", onUp);
      this.surface.addEventListener("pointercancel", onUp);
    });

    // Clicking anywhere in the pane makes it the active one. Without this the
    // host's active-pane marker doesn't follow clicks into a board, and the
    // close-pane shortcut would act on whichever pane was active before.
    this.element.addEventListener("mousedown", () => {
      send({ type: "pane.focus", paneId: this.paneId });
    });

    this.renderWaiting();
  }

  attach(host: HTMLElement) {
    host.appendChild(this.element);
    // Ask the host for this board's contents. The host answers with
    // board.state, or with board.error when the folder is unreadable.
    if (this.boardPath) send({ type: "board.request", paneId: this.paneId });
  }

  dispose() {
    this.element.remove();
  }

  setName(name: string) { this.nameEl.textContent = name; }

  setActive(active: boolean) {
    this.element.classList.toggle("pane--active", active);
  }

  /** The session's board path, pushed on every state commit. A change means a
   *  different board (or one appearing for the first time), so re-request. */
  setBoardPath(path: string) {
    if (path === this.boardPath) return;
    this.boardPath = path;
    if (path) send({ type: "board.request", paneId: this.paneId });
    else this.renderWaiting();
  }

  applyLeafView(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    this.nameEl.textContent = leaf.name;
    this.stateDotEl.dataset.state = leaf.agentState;
    this.stateLabelEl.textContent = "";
    this.colorDotEl.dataset.color = String(leaf.colorIndex);
    this.element.dataset.color = String(leaf.colorIndex);
    this.element.dataset.state = leaf.agentState;
    // A board carries no commit baseline, so the focus gate is moot — pass
    // false, exactly as UrlPane does.
    applyChips(this.branchEl, this.commitsEl, leaf, false);
  }

  // ---- tools --------------------------------------------------------------

  /** The floating control cluster: add a note, add a file, add a link, paste,
   *  then zoom. Ordered by how often you reach for it, with the two view
   *  controls fenced off after a separator because they change nothing on the
   *  board itself. */
  private buildTools(): HTMLElement {
    const bar = document.createElement("div");
    bar.className = "board__tools";
    // Never let a tool click become a pan gesture, and never let it steal the
    // pane-focus mousedown either — stopPropagation on pointerdown only, so the
    // click still lands on the button.
    bar.addEventListener("pointerdown", (ev) => ev.stopPropagation());

    const sep = document.createElement("span");
    sep.className = "board__tools-sep";

    const pct = document.createElement("button");
    pct.type = "button";
    pct.className = "board__pct";
    pct.title = "Reset view (Ctrl+0)";
    pct.setAttribute("aria-label", "Reset view");
    pct.textContent = "100%";
    pct.addEventListener("click", () => this.resetFontSize());

    bar.append(
      this.toolButton("note", "Add a note", () => this.openDraft("note")),
      this.toolButton("file", "Add a file from this project", () => this.pickFile()),
      this.toolButton("link", "Add a link", () => this.openDraft("link")),
      this.toolButton("paste", "Paste clipboard (Ctrl+V)", () => this.handlePaste()),
      sep,
      this.toolButton("minus", "Zoom out", () => this.setZoom(this.zoom / 1.1, this.viewCentre())),
      pct,
      this.toolButton("plus", "Zoom in", () => this.setZoom(this.zoom * 1.1, this.viewCentre())),
    );
    return bar;
  }

  private toolButton(icon: string, label: string, onClick: () => void): HTMLElement {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "board__tool";
    btn.title = label;
    btn.setAttribute("aria-label", label);
    btn.appendChild(svgIcon(TOOL_ICON[icon] ?? TOOL_ICON.note, 14));
    btn.addEventListener("click", (ev) => { ev.stopPropagation(); onClick(); });
    return btn;
  }

  /** Ask the host to open a file picker. The dialog can only be the host's —
   *  the page has no way to browse the disk — and the board only accepts files
   *  from inside the project, so the host roots it there. */
  private pickFile() {
    if (!this.requireBoard()) return;
    const p = this.freeSpot();
    send({ type: "board.pickFile", paneId: this.paneId, x: p.x, y: p.y });
  }

  /** Say so, once, when a control is pressed on a pane that has no board.
   *  Silence here reads as a broken button. */
  private requireBoard(): boolean {
    if (this.boardPath) return true;
    this.showBanner("This tab has no board yet.");
    return false;
  }

  // ---- adding and editing text --------------------------------------------

  /** Open a card that doesn't exist yet. Nothing is sent to the host until
   *  there's text to send: an empty note node in board.md is a line an agent
   *  has to read past, and a file write for a mis-click is a git-visible
   *  change. */
  private openDraft(kind: "note" | "link") {
    if (!this.requireBoard()) return;
    this.closeEditor();
    const p = this.freeSpot(kind === "note" ? "note" : "url");
    this.editor = { nodeId: null, kind, text: "", x: p.x, y: p.y };
    this.clearMessage();          // a draft is content; drop the empty state
    this.mountEditor();
  }

  /** Edit an existing node: a note's body, or the caption under a file, image
   *  or reference. Captions are the point for non-notes — "the broken state
   *  after login" is what makes a screenshot mean anything to an agent that
   *  only reads board.md. */
  private openEdit(n: BoardNodeView) {
    this.closeEditor();
    this.editor = { nodeId: n.id, kind: "note", text: n.text ?? "", x: n.x, y: n.y };
    this.mountEditor();
  }

  /** Put the open editor on screen. Called on open and again after every state
   *  push, because a push rebuilds every card. */
  private mountEditor() {
    const ed = this.editor;
    if (!ed) return;

    if (ed.nodeId === null) {
      this.canvas.appendChild(this.buildDraftCard(ed));
      return;
    }
    const card = this.cardFor(ed.nodeId);
    if (!card) { this.editor = null; return; }   // node removed under us

    // Replace the card's text row with a field of the same shape, so the card
    // doesn't jump when it goes editable. A card with no caption yet gets the
    // field above its path line, where the caption will end up living.
    const body = card.querySelector<HTMLElement>(".board-node__body[data-caption]");
    const ref = card.querySelector<HTMLElement>(".board-node__ref");
    const field = this.buildField(ed, card);
    if (body) body.replaceWith(field);
    else if (ref) card.insertBefore(field, ref);
    else card.appendChild(field);
    field.after(this.buildEditorActions(card, "Save"));
    card.classList.add("board-node--editing");
    focusEnd(field);
  }

  /** A card for something not yet on the board: same frame as a real card so
   *  the result of pressing "note" looks like what you'll get. */
  private buildDraftCard(ed: { kind: "note" | "link"; x: number; y: number }): HTMLElement {
    const el = document.createElement("div");
    const kind = ed.kind === "note" ? "note" : "url";
    el.className = `board-node board-node--${kind} board-node--draft board-node--editing`;
    const def = DEFAULT_SIZE[kind];
    el.style.left = `${ed.x}px`;
    el.style.top = `${ed.y}px`;
    el.style.width = `${def.w}px`;
    el.style.height = `${def.h}px`;

    const bar = document.createElement("div");
    bar.className = "board-node__bar";
    bar.appendChild(kindIcon(kind));
    const kindEl = document.createElement("span");
    kindEl.textContent = ed.kind === "note" ? "new note" : "new link";
    bar.append(kindEl);
    el.appendChild(bar);

    el.appendChild(this.buildField(this.editor!, el));
    el.appendChild(this.buildEditorActions(el, "Add"));
    // Draft cards are not draggable — there is nothing to move yet — so the
    // pan handler must not treat this as empty space either. .board-node in the
    // class list already covers that.
    setTimeout(() => focusEnd(el.querySelector(".board-node__field") as HTMLElement), 0);
    return el;
  }

  /** The actual input. A note gets a textarea (notes wrap and have paragraphs);
   *  a link gets a single-line input, where Enter can mean commit. */
  private buildField(
    ed: { nodeId: string | null; kind: "note" | "link"; text: string },
    card: HTMLElement,
  ): HTMLElement {
    const multiline = ed.kind === "note";
    const field = document.createElement(multiline ? "textarea" : "input") as
      HTMLTextAreaElement | HTMLInputElement;
    // A union-typed element loses the typed addEventListener overloads, so the
    // listeners below bind through this HTMLElement view of the same node.
    const el: HTMLElement = field;
    field.className = "board-node__field";
    field.value = ed.text;
    field.placeholder = multiline
      ? (ed.nodeId ? "" : "Type a note…")
      : "https://…";
    field.spellcheck = false;

    // Keep the model in step with every keystroke, so a state push mid-typing
    // can re-mount the editor with what's actually been typed.
    field.addEventListener("input", () => { if (this.editor) this.editor.text = field.value; });

    el.addEventListener("keydown", (ev) => {
      if (ev.key === "Escape") {
        ev.stopPropagation();          // don't let Esc reach the dashboard
        this.cancelEditor(card);
        return;
      }
      // Commit on Enter for a link, Ctrl+Enter for a note (plain Enter there is
      // a newline, which is the whole reason a note is a textarea).
      if (ev.key === "Enter" && (!multiline || ev.ctrlKey)) {
        ev.preventDefault();
        ev.stopPropagation();
        this.commitEditor(card);
      }
      // Everything else stays local: the app's shortcuts are on document in
      // capture phase and would eat Ctrl+A, arrows and the like.
      ev.stopPropagation();
    });

    // Clicking away commits. The alternative — discarding — throws away typing
    // for a mis-click, which is the worse failure.
    field.addEventListener("blur", () => {
      if (this.editor && this.editorCard() === card) this.commitEditor(card);
    });
    // A field must own its own pointer events or the card's drag steals them.
    field.addEventListener("pointerdown", (ev) => ev.stopPropagation());
    return field;
  }

  /** Commit / cancel under an open field.
   *
   *  Not decoration: blur-to-commit and Ctrl+Enter are both invisible, and an
   *  editor whose only exits are gestures you have to already know is an editor
   *  people abandon. `label` is "Add" for a card that doesn't exist yet and
   *  "Save" for one that does, because those are different promises. */
  private buildEditorActions(card: HTMLElement, label: string): HTMLElement {
    const row = document.createElement("div");
    row.className = "board-node__editrow";

    const hint = document.createElement("span");
    hint.className = "board-node__edithint";
    hint.textContent = "Ctrl+Enter";

    const cancel = editorButton("Cancel", () => this.cancelEditor(card));
    const commit = editorButton(label, () => this.commitEditor(card));
    commit.classList.add("board-node__editbtn--primary");

    row.append(hint, cancel, commit);
    return row;
  }

  private editorCard(): HTMLElement | null {
    return this.canvas.querySelector<HTMLElement>(".board-node--editing");
  }

  private cardFor(nodeId: string): HTMLElement | null {
    return this.canvas.querySelector<HTMLElement>(
      `.board-node[data-node-id="${cssEscape(nodeId)}"]`);
  }

  /** Send what was typed, if anything, and put the card back. */
  private commitEditor(card: HTMLElement) {
    const ed = this.editor;
    if (!ed) return;
    this.editor = null;                          // before send: the state push
    const text = ed.text.trim();                 // that follows must not re-mount

    if (ed.nodeId === null) {
      card.remove();
      if (text.length === 0) { this.restoreEmptyState(); return; }
      // A note is forced; a link goes through the host's classifier so a path
      // or a plain sentence typed into the link box still lands as something.
      send({
        type: "board.add", paneId: this.paneId,
        kind: ed.kind === "note" ? "note" : "auto",
        text, x: ed.x, y: ed.y,
      });
      return;
    }
    send({ type: "board.edit", paneId: this.paneId, nodeId: ed.nodeId, text });
    // The host echoes full state after the write; until then, show what was
    // typed rather than an input the user has finished with.
    this.demoteField(card, text);
  }

  private cancelEditor(card: HTMLElement) {
    const ed = this.editor;
    this.editor = null;
    if (!ed) return;
    if (ed.nodeId === null) { card.remove(); this.restoreEmptyState(); return; }
    this.demoteField(card, ed.text);
    // Re-request rather than trusting our own repaint: the node's real text is
    // the host's, and cancelling should show exactly that.
    if (this.boardPath) send({ type: "board.request", paneId: this.paneId });
  }

  /** Turn an open field back into a static text row. */
  private demoteField(card: HTMLElement, text: string) {
    card.classList.remove("board-node--editing");
    card.querySelector(".board-node__editrow")?.remove();
    const field = card.querySelector<HTMLElement>(".board-node__field");
    if (!field) return;
    const isNote = card.classList.contains("board-node--note");
    if (!isNote && text.length === 0) { field.remove(); return; }
    const body = document.createElement("div");
    body.className = "board-node__body";
    body.dataset.caption = "1";
    body.textContent = text || "(empty note)";
    field.replaceWith(body);
  }

  /** Drop any open editor without sending. Used when another one opens. */
  private closeEditor() {
    if (!this.editor) return;
    const card = this.editorCard();
    if (card) this.cancelEditor(card);
    else this.editor = null;
  }

  /** Back to the empty state after a draft was abandoned on a bare board. */
  private restoreEmptyState() {
    if (this.nodes.length === 0 && !this.canvas.firstChild) this.renderEmpty();
  }

  // ---- host pushes --------------------------------------------------------

  /** Render the whole board. The host sends the complete state after every
   *  mutation rather than a diff — a board is tens of nodes, and a full
   *  replace can't drift from the file. */
  applyBoardState(nodes: BoardNodeView[], links: BoardLinkView[]) {
    this.clearMessage();
    this.canvas.replaceChildren();
    this.nodes = nodes;

    // An open draft counts as content: showing "nothing on this board yet"
    // underneath the note someone is typing would be its own small joke.
    if (nodes.length === 0 && !this.editor) { this.renderEmpty(); return; }

    for (const n of nodes) this.canvas.appendChild(this.buildNode(n));
    // Ask for previews only for images we don't already hold. A state push
    // happens after every mutation, and re-fetching every picture each time
    // would make dragging one card re-encode all of them.
    for (const n of nodes) {
      if (n.kind !== "image") continue;
      if (this.imageCache.has(n.id)) this.paintImage(n.id, this.imageCache.get(n.id)!);
      else send({ type: "board.image", paneId: this.paneId, nodeId: n.id });
    }
    // An open editor survives the rebuild. A push arrives after ANY mutation in
    // the tab — a fetch finishing, the other window onto this board moving a
    // card — and none of those are a reason to lose a half-typed note.
    this.mountEditor();
    // Links are rendered in a later phase; accepted here so the message shape
    // is stable from the start.
    void links;
  }

  /** A card-sized preview arrived. Empty data means the file is gone. */
  applyImage(nodeId: string, dataUrl: string) {
    if (dataUrl) this.imageCache.set(nodeId, dataUrl);
    this.paintImage(nodeId, dataUrl);
  }

  private paintImage(nodeId: string, dataUrl: string) {
    const slot = this.canvas.querySelector<HTMLElement>(
      `.board-node[data-node-id="${cssEscape(nodeId)}"] .board-node__shot`
    );
    if (!slot) return;
    slot.replaceChildren();
    if (!dataUrl) {
      slot.classList.add("board-node__shot--missing");
      slot.textContent = "image missing";
      return;
    }
    slot.classList.remove("board-node__shot--missing");
    const img = document.createElement("img");
    img.className = "board-node__img";
    img.src = dataUrl;
    img.draggable = false;   // the card owns dragging, via pointer events
    img.alt = "";
    slot.appendChild(img);
  }

  /** Something went wrong. Two very different cases share this one message, and
   *  only the HOST can tell them apart (see BoardController.Failed):
   *
   *   - fatal → the board can't be opened at all. The full-surface explanation,
   *     because an empty grid looks exactly like a board nobody has filled in
   *     yet, and every card we're still holding is stale.
   *   - otherwise → one action failed (a fetch 404'd, a picked file was outside
   *     the project). A strip: blanking a board full of work to report that one
   *     paste didn't land is a far bigger loss than the error itself, and the
   *     first version did exactly that.
   */
  showError(message: string, fatal = false) {
    if (fatal) {
      this.nodes = [];
      this.editor = null;
      this.renderMessage("This board can’t be opened", message, this.boardPath);
      return;
    }
    this.showBanner(message);
  }

  /** Transient error strip along the bottom of the surface. */
  private showBanner(message: string) {
    if (!this.bannerEl) {
      const el = document.createElement("div");
      el.className = "board-banner";
      const text = document.createElement("span");
      text.className = "board-banner__text";
      const close = document.createElement("button");
      close.type = "button";
      close.className = "board-banner__close";
      close.title = "Dismiss";
      close.setAttribute("aria-label", "Dismiss");
      close.textContent = "✕";
      close.addEventListener("click", () => this.hideBanner());
      el.append(text, close);
      this.surface.appendChild(el);
      this.bannerEl = el;
    }
    (this.bannerEl.querySelector(".board-banner__text") as HTMLElement).textContent = message;
    this.bannerEl.title = message;
    this.bannerEl.classList.add("board-banner--on");
    clearTimeout(this.bannerTimer);
    this.bannerTimer = window.setTimeout(() => this.hideBanner(), BANNER_MS);
  }

  private hideBanner() {
    clearTimeout(this.bannerTimer);
    this.bannerEl?.classList.remove("board-banner--on");
  }

  // ---- rendering ----------------------------------------------------------

  private renderWaiting() {
    this.renderMessage("", this.boardPath ? "Opening board…" : "This tab has no board.", "");
  }

  private renderEmpty() {
    this.surface.classList.add("board--message");
    const box = document.createElement("div");
    box.dataset.boardMessage = "1";
    box.className = "board-message";

    const title = document.createElement("div");
    title.className = "board-message__title";
    title.textContent = "Nothing on this board yet";

    const body = document.createElement("div");
    body.className = "board-message__body";
    body.textContent = "Paste a screenshot, add a file, drop in a link, or write a note.";

    // Buttons, not just a keyboard hint: an empty board is exactly where you
    // don't yet know what this surface takes, and the tools in the corner are
    // small. Same three actions the toolbar has, spelled out.
    const actions = document.createElement("div");
    actions.className = "board-message__actions";
    actions.append(
      messageButton("Add a note", () => this.openDraft("note")),
      messageButton("Add a file", () => this.pickFile()),
      messageButton("Add a link", () => this.openDraft("link")),
    );

    const hint = document.createElement("div");
    hint.className = "board-message__hint";
    for (const k of ["Ctrl", "V"]) {
      const kbd = document.createElement("kbd");
      kbd.textContent = k;
      hint.appendChild(kbd);
    }
    const hintText = document.createElement("span");
    hintText.textContent = "to paste";
    hint.appendChild(hintText);

    box.append(title, body, actions, hint);
    this.surface.appendChild(box);
  }

  private renderMessage(title: string, body: string, detail: string) {
    this.clearMessage();
    this.canvas.replaceChildren();
    this.surface.classList.add("board--message");
    const box = document.createElement("div");
    box.dataset.boardMessage = "1";
    box.className = "board-message";
    if (title) {
      const t = document.createElement("div");
      t.className = "board-message__title";
      t.textContent = title;
      box.appendChild(t);
    }
    const b = document.createElement("div");
    b.className = "board-message__body";
    b.textContent = body;
    box.appendChild(b);
    if (detail) {
      const d = document.createElement("div");
      d.className = "board-message__detail";
      d.textContent = detail;
      d.title = detail;
      box.appendChild(d);
    }
    this.surface.appendChild(box);
  }

  private buildNode(n: BoardNodeView): HTMLElement {
    const el = document.createElement("div");
    el.className = `board-node board-node--${n.kind}`;
    el.dataset.nodeId = n.id;
    const { w, h } = sizeOf(n);
    el.style.left = `${n.x}px`;
    el.style.top = `${n.y}px`;
    el.style.width = `${w}px`;
    el.style.height = `${h}px`;

    const bar = document.createElement("div");
    bar.className = "board-node__bar";
    bar.appendChild(kindIcon(n.kind));
    const kindEl = document.createElement("span");
    kindEl.textContent = KIND_LABEL[n.kind] ?? n.kind;
    bar.appendChild(kindEl);
    const spacer = document.createElement("span");
    spacer.className = "board-node__spacer";
    bar.appendChild(spacer);
    bar.appendChild(this.buildRemove(n.id));
    el.appendChild(bar);

    // Dragging and resizing are POINTER events with capture, the way the split
    // gutter does it — not HTML5 drag. The pane element is already an HTML5
    // drop target for pane-rearrange (wirePaneDnd), and sharing that channel
    // between two drag systems on the same element invites exactly the kind of
    // bug that only shows up mid-gesture.
    this.wireDrag(el, n.id);
    el.appendChild(this.buildResizeHandle(el, n.id));

    // Double-click to edit: a note's body, or the caption on anything else.
    // Double-click rather than single, because a single click on a card is the
    // start of a drag and turning that into an editor would fight the gesture.
    el.addEventListener("dblclick", (ev) => {
      if ((ev.target as HTMLElement).closest(".board-node__resize, .board-node__remove")) return;
      ev.stopPropagation();
      this.openEdit(n);
    });

    // An image leads with the picture: that IS the content, and a filename
    // tells you nothing about which screenshot this is.
    if (n.kind === "image") {
      const shot = document.createElement("div");
      shot.className = "board-node__shot";
      el.appendChild(shot);
    }

    // A note IS its text; everything else leads with the artifact it resolved
    // to, because that path is what the agent will open.
    if (n.kind === "note") {
      const body = document.createElement("div");
      body.className = "board-node__body";
      // Marked as THE editable row: a url card has two more .board-node__body
      // lines (its caption and its provenance) and an editor that replaced the
      // wrong one would look like the card had lost its source.
      body.dataset.caption = "1";
      body.textContent = n.text || "(empty note)";
      el.appendChild(body);
      return el;
    }

    const title = document.createElement("div");
    title.className = "board-node__title";
    title.textContent = basename(n.ref ?? "") || n.text || "(unresolved)";
    title.title = n.ref ?? "";
    el.appendChild(title);

    if (n.text) {
      const body = document.createElement("div");
      body.className = "board-node__body";
      body.dataset.caption = "1";
      body.textContent = n.text;
      el.appendChild(body);
    }

    if (n.kind === "url" && n.source) {
      const src = document.createElement("div");
      src.className = "board-node__body";
      src.textContent = n.fetchedUtc ? `fetched ${n.fetchedUtc}` : n.source;
      src.title = n.source;
      el.appendChild(src);
    }

    if (n.ref) {
      const ref = document.createElement("div");
      ref.className = "board-node__ref";
      ref.textContent = n.ref;
      ref.title = n.ref;
      el.appendChild(ref);
    }
    return el;
  }

  /** Drop any empty/waiting/error box. Messages live on the SURFACE, above the
   *  zoom layer, so they stay legible whatever the zoom is — a 40%-scale "this
   *  board can't be opened" would be its own small joke. */
  private clearMessage() {
    for (const el of this.surface.querySelectorAll("[data-board-message]")) el.remove();
    this.surface.classList.remove("board--message");
  }

  // ---- zoom ---------------------------------------------------------------

  /** Set the view scale, optionally keeping `anchor` (a point in SURFACE
   *  coordinates) over the same board-space point it was over before. That is
   *  what makes Ctrl+wheel feel like zooming toward the cursor instead of
   *  toward the corner. */
  private setZoom(next: number, anchor?: { x: number; y: number }) {
    const z = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, next));
    if (z === this.zoom) return;
    if (anchor) {
      // Board-space point under the anchor before and after; shift the pan by
      // the difference so it doesn't move on screen.
      const bx = (anchor.x - this.panX) / this.zoom;
      const by = (anchor.y - this.panY) / this.zoom;
      this.panX = anchor.x - bx * z;
      this.panY = anchor.y - by * z;
    }
    this.zoom = z;
    this.applyTransform();
  }

  private applyTransform() {
    this.canvas.style.transform =
      `translate(${this.panX}px, ${this.panY}px) scale(${this.zoom})`;
    // Scale the dotted grid with the content, else the background stays put
    // while the cards move and the whole surface reads as broken.
    this.surface.style.backgroundSize = `${16 * this.zoom}px ${16 * this.zoom}px`;
    this.surface.style.backgroundPosition = `${this.panX}px ${this.panY}px`;
    // The readout is permanent now that it lives in the toolbar and doubles as
    // the reset button. Zoom used to be invisible state announced by a badge
    // that faded; a control you can point at is better than a hint that leaves.
    this.zoomPctEl.textContent = `${Math.round(this.zoom * 100)}%`;
  }

  /** Centre of the surface, in surface coordinates — the anchor for keyboard
   *  zoom, since there's no pointer involved. */
  private viewCentre() {
    return { x: this.surface.clientWidth / 2, y: this.surface.clientHeight / 2 };
  }

  // ---- gestures -----------------------------------------------------------

  /** Drag a card by its body. Pointer events + setPointerCapture, so the drag
   *  survives the pointer leaving the element and can't be interrupted by
   *  another element's hover — the same mechanics beginGutterDrag uses.
   *
   *  Sends a stream of final:false moves while dragging and one final:true on
   *  release; only the last one writes board.md. */
  private wireDrag(el: HTMLElement, nodeId: string) {
    el.addEventListener("pointerdown", (ev) => {
      // Left button only, and never from a control that has its own job.
      if (ev.button !== 0) return;
      const target = ev.target as HTMLElement;
      if (target.closest(".board-node__resize, .board-node__remove")) return;

      const startX = ev.clientX, startY = ev.clientY;
      const originX = parseFloat(el.style.left) || 0;
      const originY = parseFloat(el.style.top) || 0;
      let moved = false;

      el.setPointerCapture(ev.pointerId);
      el.classList.add("board-node--dragging");
      ev.preventDefault();

      const onMove = (m: PointerEvent) => {
        const dx = m.clientX - startX, dy = m.clientY - startY;
        // A few pixels of slop so a click that happens to jitter doesn't
        // register as a drag and rewrite the file. Measured on SCREEN, so the
        // threshold feels the same at any zoom.
        if (!moved && Math.abs(dx) < 3 && Math.abs(dy) < 3) return;
        moved = true;
        // Pointer movement is screen-space; card coordinates are board-space.
        // Without dividing by zoom, a card zoomed to 50% would travel twice as
        // far as the cursor.
        const p = this.clamp(originX + dx / this.zoom, originY + dy / this.zoom, el);
        el.style.left = `${p.x}px`;
        el.style.top = `${p.y}px`;
        send({ type: "board.move", paneId: this.paneId, nodeId, x: p.x, y: p.y, final: false });
      };
      const onUp = () => {
        el.releasePointerCapture(ev.pointerId);
        el.classList.remove("board-node--dragging");
        el.removeEventListener("pointermove", onMove);
        el.removeEventListener("pointerup", onUp);
        el.removeEventListener("pointercancel", onUp);
        if (!moved) return;
        send({
          type: "board.move", paneId: this.paneId, nodeId,
          x: parseFloat(el.style.left) || 0, y: parseFloat(el.style.top) || 0, final: true,
        });
      };
      el.addEventListener("pointermove", onMove);
      el.addEventListener("pointerup", onUp);
      el.addEventListener("pointercancel", onUp);
    });
  }

  private buildResizeHandle(el: HTMLElement, nodeId: string): HTMLElement {
    const grip = document.createElement("div");
    grip.className = "board-node__resize";
    grip.title = "Resize";
    grip.addEventListener("pointerdown", (ev) => {
      if (ev.button !== 0) return;
      ev.stopPropagation();          // don't start a drag as well
      ev.preventDefault();
      const startX = ev.clientX, startY = ev.clientY;
      const w0 = el.offsetWidth, h0 = el.offsetHeight;
      grip.setPointerCapture(ev.pointerId);
      el.classList.add("board-node--resizing");

      const onMove = (m: PointerEvent) => {
        // Same board-space conversion as the drag: the grip should stay under
        // the cursor at any zoom.
        const w = Math.min(MAX_W, Math.max(MIN_W, w0 + (m.clientX - startX) / this.zoom));
        const h = Math.min(MAX_H, Math.max(MIN_H, h0 + (m.clientY - startY) / this.zoom));
        el.style.width = `${w}px`;
        el.style.height = `${h}px`;
        send({ type: "board.resize", paneId: this.paneId, nodeId, w, h, final: false });
      };
      const onUp = () => {
        grip.releasePointerCapture(ev.pointerId);
        el.classList.remove("board-node--resizing");
        grip.removeEventListener("pointermove", onMove);
        grip.removeEventListener("pointerup", onUp);
        grip.removeEventListener("pointercancel", onUp);
        send({
          type: "board.resize", paneId: this.paneId, nodeId,
          w: el.offsetWidth, h: el.offsetHeight, final: true,
        });
      };
      grip.addEventListener("pointermove", onMove);
      grip.addEventListener("pointerup", onUp);
      grip.addEventListener("pointercancel", onUp);
    });
    return grip;
  }

  private buildRemove(nodeId: string): HTMLElement {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "board-node__remove";
    btn.title = "Remove from board";
    btn.setAttribute("aria-label", "Remove from board");
    btn.textContent = "✕";
    btn.addEventListener("pointerdown", (ev) => ev.stopPropagation());
    btn.addEventListener("click", (ev) => {
      ev.stopPropagation();
      send({ type: "board.remove", paneId: this.paneId, nodeId });
    });
    return btn;
  }

  /** Keep a card inside the visible area. Without this a card can be dragged
   *  past the edge and become unreachable — there is no scrolling to go find
   *  it. Bounds are in BOARD space, so the visible extent grows as you zoom
   *  out: at 50% there is twice as much room to put things. */
  private clamp(x: number, y: number, el: HTMLElement): { x: number; y: number } {
    const viewW = (this.surface.clientWidth - this.panX) / this.zoom;
    const viewH = (this.surface.clientHeight - this.panY) / this.zoom;
    const maxX = Math.max(0, viewW - el.offsetWidth);
    const maxY = Math.max(0, viewH - el.offsetHeight);
    return {
      x: Math.round(Math.min(Math.max(0, x), maxX)),
      y: Math.round(Math.min(Math.max(0, y), maxY)),
    };
  }

  /** A paste landed while this pane was active. Sends only the drop point —
   *  the host reads the clipboard itself.
   *
   *  Returns whether the board TOOK it. False while an editor is open, so
   *  Ctrl+V into a note being typed pastes text into the note instead of
   *  becoming a second card — the caller only calls preventDefault when this
   *  says yes. */
  handlePaste(): boolean {
    if (!this.boardPath || this.editor) return false;
    const p = this.freeSpot();
    send({ type: "board.paste", paneId: this.paneId, x: p.x, y: p.y });
    return true;
  }

  /** Somewhere to put a new card that isn't on top of an existing one. Walks a
   *  coarse grid and takes the first free cell; falls back to the top-left when
   *  the board is full, which is better than refusing to add the thing.
   *
   *  Anchored to what's ON SCREEN, not to board (0,0): after panning or zooming
   *  out, board space's origin can be far outside the viewport, and a new card
   *  you can't see is a card you'll assume never got added. */
  private freeSpot(kind = "note"): { x: number; y: number } {
    const step = 232, rowStep = 168, padding = 16;
    const originX = Math.round(-this.panX / this.zoom) + padding;
    const originY = Math.round(-this.panY / this.zoom) + padding;
    const cols = Math.max(1, Math.floor(
      (this.surface.clientWidth / this.zoom - padding) / step));
    const size = DEFAULT_SIZE[kind] ?? DEFAULT_SIZE.note;
    for (let i = 0; i < 200; i++) {
      const x = originX + (i % cols) * step;
      const y = originY + Math.floor(i / cols) * rowStep;
      // Rectangle overlap against each card's ACTUAL size — not a proximity
      // test on origins, which ignores how tall a card is: a 250px-tall
      // screenshot spans two grid rows, and comparing only top-left corners
      // called the row below it free and dropped the new card on top of it.
      const clash = this.nodes.some((n) => {
        const s = sizeOf(n);
        return x < n.x + s.w && n.x < x + size.w
            && y < n.y + s.h && n.y < y + size.h;
      });
      if (!clash) return { x, y };
    }
    return { x: originX, y: originY };
  }

  // ---- LeafPane members ----------------------------------------------------

  feed(_b64: string) { /* no terminal to feed */ }
  notifyExit(_code: number) { /* boards don't exit */ }
  focus() { /* nothing focusable yet; the surface is not an input */ }
  forceRefit() { /* absolutely-positioned cards; nothing to re-measure */ }
  setFontSize(_size: number) { /* a board's type is chrome, not content */ }

  /** Ctrl+= / Ctrl+- reach every leaf kind through the same call. On a
   *  terminal they change the font; on a board the equivalent gesture is
   *  ZOOM, so the standard shortcut does the standard thing here too.
   *
   *  Returns 0 deliberately: a non-zero return is persisted by main.ts as the
   *  global terminal font-size preference, and zooming a board must not
   *  resize every terminal in the app. */
  changeFontSize(delta: number): number {
    this.setZoom(this.zoom * (delta > 0 ? 1.1 : 1 / 1.1), this.viewCentre());
    return 0;
  }

  /** Ctrl+0 — back to 100% AND back to the origin, because a zoomed-and-panned
   *  board with everything off-screen is exactly when you reach for reset. */
  resetFontSize(): number {
    this.panX = 0;
    this.panY = 0;
    this.zoom = 1;
    this.applyTransform();
    return 0;
  }
}

function kindIcon(kind: string): SVGSVGElement {
  return svgIcon(KIND_ICON[kind] ?? KIND_ICON.note, 12, "board-node__icon");
}

/** One single-stroke Fluent-family glyph from a 24x24 path. */
function svgIcon(d: string, size: number, cls = "board__tool-icon"): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", cls);
  svg.setAttribute("width", String(size));
  svg.setAttribute("height", String(size));
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", d);
  svg.appendChild(path);
  return svg;
}

/** A button under an open editor. preventDefault on pointerdown is what makes
 *  "Cancel" actually cancel: without it the mousedown moves focus out of the
 *  field first, blur commits, and the click lands on an editor that has already
 *  saved. */
function editorButton(label: string, onClick: () => void): HTMLElement {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "board-node__editbtn";
  btn.textContent = label;
  btn.addEventListener("pointerdown", (ev) => { ev.preventDefault(); ev.stopPropagation(); });
  btn.addEventListener("click", (ev) => { ev.stopPropagation(); onClick(); });
  return btn;
}

/** A text button for the empty state. */
function messageButton(label: string, onClick: () => void): HTMLElement {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "board-message__btn";
  btn.textContent = label;
  btn.addEventListener("pointerdown", (ev) => ev.stopPropagation());
  btn.addEventListener("click", (ev) => { ev.stopPropagation(); onClick(); });
  return btn;
}

/** Focus a field and put the caret after the existing text, so editing an
 *  existing note continues it rather than replacing it. */
function focusEnd(el: HTMLElement | null) {
  if (!el) return;
  const field = el as HTMLTextAreaElement | HTMLInputElement;
  field.focus();
  try { field.setSelectionRange(field.value.length, field.value.length); }
  catch { /* not a text field; focus alone is enough */ }
}

/** Last path segment, for the card title. Exported for tests. */
export function basename(p: string): string {
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i < 0 ? p : p.slice(i + 1);
}

/** Quote a node id for use inside an attribute selector. Ids are ours ("n7"),
 *  but they also survive round trips through a file a human can edit, so don't
 *  assume. CSS.escape isn't in the older lib target. */
function cssEscape(s: string): string {
  return s.replace(/["\\]/g, "\\$&");
}
