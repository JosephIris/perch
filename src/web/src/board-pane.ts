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
  private readonly zoomBadge: HTMLElement;
  /** View scale. A VIEW preference, so it lives in memory and never reaches
   *  board.md — how far you happen to be zoomed in is not content, and writing
   *  it would make every zoom a file write and a git-visible change. */
  private zoom = 1;
  /** Pan offset in board space, moved to keep a zoom anchored under the
   *  pointer. */
  private panX = 0;
  private panY = 0;
  private zoomBadgeTimer = 0;
  private boardPath = "";
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

    this.zoomBadge = document.createElement("div");
    this.zoomBadge.className = "board__zoom";
    this.surface.appendChild(this.zoomBadge);

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
      if ((ev.target as HTMLElement).closest(".board-node")) return;  // card drag owns it
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

  // ---- host pushes --------------------------------------------------------

  /** Render the whole board. The host sends the complete state after every
   *  mutation rather than a diff — a board is tens of nodes, and a full
   *  replace can't drift from the file. */
  applyBoardState(nodes: BoardNodeView[], links: BoardLinkView[]) {
    this.clearMessage();
    this.canvas.replaceChildren();
    this.nodes = nodes;

    if (nodes.length === 0) { this.renderEmpty(); return; }

    for (const n of nodes) this.canvas.appendChild(this.buildNode(n));
    // Ask for previews only for images we don't already hold. A state push
    // happens after every mutation, and re-fetching every picture each time
    // would make dragging one card re-encode all of them.
    for (const n of nodes) {
      if (n.kind !== "image") continue;
      if (this.imageCache.has(n.id)) this.paintImage(n.id, this.imageCache.get(n.id)!);
      else send({ type: "board.image", paneId: this.paneId, nodeId: n.id });
    }
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

  /** The host could not read this board. Say why — an empty grid would look
   *  exactly like a board nobody has put anything on yet. */
  showError(message: string) {
    this.renderMessage("This board can’t be opened", message, this.boardPath);
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
    body.textContent = "Paste a screenshot, a file path, a link, or type a note.";

    const hint = document.createElement("div");
    hint.className = "board-message__hint";
    for (const k of ["Ctrl", "V"]) {
      const kbd = document.createElement("kbd");
      kbd.textContent = k;
      hint.appendChild(kbd);
    }
    box.append(title, body, hint);
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
    this.flashZoomBadge();
  }

  private applyTransform() {
    this.canvas.style.transform =
      `translate(${this.panX}px, ${this.panY}px) scale(${this.zoom})`;
    // Scale the dotted grid with the content, else the background stays put
    // while the cards move and the whole surface reads as broken.
    this.surface.style.backgroundSize = `${16 * this.zoom}px ${16 * this.zoom}px`;
    this.surface.style.backgroundPosition = `${this.panX}px ${this.panY}px`;
  }

  /** Show the percentage briefly after a change. Zoom is otherwise invisible
   *  state — without this, "why is everything small" has no answer on screen. */
  private flashZoomBadge() {
    this.zoomBadge.textContent = `${Math.round(this.zoom * 100)}%`;
    this.zoomBadge.classList.add("board__zoom--on");
    clearTimeout(this.zoomBadgeTimer);
    this.zoomBadgeTimer = window.setTimeout(
      () => this.zoomBadge.classList.remove("board__zoom--on"), 1100);
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
   *  the host reads the clipboard itself. */
  handlePaste() {
    if (!this.boardPath) return;
    const p = this.freeSpot();
    send({ type: "board.paste", paneId: this.paneId, x: p.x, y: p.y });
  }

  /** Somewhere to put a new card that isn't on top of an existing one. Walks a
   *  coarse grid and takes the first free cell; falls back to the origin when
   *  the board is full, which is better than refusing to add the thing. */
  private freeSpot(): { x: number; y: number } {
    const step = 232, padding = 16;
    const cols = Math.max(1, Math.floor((this.surface.clientWidth - padding) / step));
    const taken = new Set(this.nodes.map((n) => `${Math.round(n.x)},${Math.round(n.y)}`));
    for (let i = 0; i < 200; i++) {
      const x = padding + (i % cols) * step;
      const y = padding + Math.floor(i / cols) * 168;
      if (!taken.has(`${x},${y}`)) return { x, y };
    }
    return { x: padding, y: padding };
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
    this.flashZoomBadge();
    return 0;
  }
}

function kindIcon(kind: string): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", "board-node__icon");
  svg.setAttribute("width", "12");
  svg.setAttribute("height", "12");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", KIND_ICON[kind] ?? KIND_ICON.note);
  svg.appendChild(path);
  return svg;
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
