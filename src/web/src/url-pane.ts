// UrlPane = a leaf pane that delegates content rendering to a real
// WebView2 control on the WPF side. The page just owns the rect and
// reports it; the host creates/positions/disposes a WebView2 in a
// Canvas overlay above the main webview.
//
// Why not an iframe? Most real-world sites (Google, GitHub, banks) set
// `X-Frame-Options: DENY` or CSP `frame-ancestors`, which makes iframes
// blank. A native WebView2 isn't an iframe — it's a full browser
// instance with no embedder restrictions.

import { send } from "./bridge.js";
import type { PaneTreeView } from "./bridge.js";
import { buildPaneHeader, applyChips } from "./pane-header.js";

export class UrlPane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly nameEl: HTMLElement;
  private readonly stateDotEl: HTMLElement;
  private readonly stateLabelEl: HTMLElement;
  private readonly colorDotEl: HTMLElement;
  private readonly branchEl: HTMLElement;
  private readonly commitsEl: HTMLElement;
  private readonly slot: HTMLElement;
  private readonly content: HTMLElement;
  private readonly url: string;
  private observer?: ResizeObserver;
  private lastRect = { x: -1, y: -1, w: -1, h: -1 };
  private rafId = 0;

  constructor(paneId: string, name: string, url: string) {
    this.paneId = paneId;
    this.url = url;

    this.element = document.createElement("div");
    this.element.className = "pane pane--url";
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

    // Slot div takes up the body space of the pane and provides the
    // padding between the pane border and the URL content (mirrors the
    // terminal pane's padding). The actual WebView2 child window is
    // positioned over the INNER content div, so the padding visibly
    // separates the WebView2 from the pane edges.
    this.slot = document.createElement("div");
    this.slot.className = "pane__urlslot";
    this.element.appendChild(this.slot);

    this.content = document.createElement("div");
    this.content.className = "pane__urlcontent";
    this.slot.appendChild(this.content);

    this.element.addEventListener("mousedown", () => this.notifyFocus());
  }

  attach(host: HTMLElement) {
    host.appendChild(this.element);
    // Observe the inner content for size + position changes. Browsers
    // fire ResizeObserver on size changes; position changes (e.g. sibling
    // resized, sidebar toggled) don't trigger it, so we ALSO poll on
    // window resize. We watch .content (not .slot) because that's the
    // INSET rect the WebView2 should occupy — slot includes the padding.
    this.observer = new ResizeObserver(() => this.reportLayout());
    this.observer.observe(this.content);
    // Initial layout — one rAF so flex has finished sizing.
    requestAnimationFrame(() => this.reportLayout());
    window.addEventListener("resize", this.scheduleReport);
  }

  dispose() {
    this.observer?.disconnect();
    this.observer = undefined;
    window.removeEventListener("resize", this.scheduleReport);
    // beginLeaving() may have already sent urlpane.dispose; sending again
    // is harmless (host TryGetValue returns false for the second one).
    if (!this.tornDown) send({ type: "urlpane.dispose", paneId: this.paneId });
    this.element.remove();
  }

  private tornDown = false;
  /** Called by workspace.ts at the START of the leaving animation so the
   *  WebView2 child window closes immediately, in sync with the placeholder
   *  fade — not 200ms later when dispose() actually fires. Without this
   *  the WebView2 sits at full opacity over the fading placeholder. */
  beginLeaving() {
    if (this.tornDown) return;
    this.tornDown = true;
    send({ type: "urlpane.dispose", paneId: this.paneId });
  }

  setName(name: string) { this.nameEl.textContent = name; }
  setActive(active: boolean) {
    this.element.classList.toggle("pane--active", active);
  }

  feed(_b64: string) { /* no-op for URL panes */ }
  notifyExit(_code: number) { /* no-op for URL panes */ }
  /** Called when this leaf's parent split changes or the host triggers
   *  ui.urlpane.relayout. Invalidates the cached rect so reportLayout
   *  always re-sends. Double-rAF deferral so the CSS layout pass has
   *  fully settled before we read getBoundingClientRect — without this,
   *  a host-triggered relayout right after window resize can read the
   *  rect mid-reflow and report stale dims, leaving the WebView2 at the
   *  wrong padding inset. */
  forceRefit() {
    this.lastRect = { x: -1, y: -1, w: -1, h: -1 };
    requestAnimationFrame(() =>
      requestAnimationFrame(() => this.reportLayout())
    );
  }
  focus() { /* native WebView2 handles its own focus */ }
  changeFontSize(_delta: number) { /* no-op */ }
  resetFontSize() { /* no-op */ }

  applyLeafView(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    this.nameEl.textContent = leaf.name;
    this.stateDotEl.dataset.state = leaf.agentState;
    this.stateLabelEl.textContent =
      leaf.agentState === "idle" ? "" : leaf.agentState;
    this.colorDotEl.dataset.color = String(leaf.colorIndex);
    this.element.dataset.color = String(leaf.colorIndex);
    applyChips(this.branchEl, this.commitsEl, leaf);
    // The URL itself doesn't change once a pane is created — but if it
    // ever does (future feature), we'd want to re-navigate. The layout
    // message carries the URL on every push so the host can detect.
    this.reportLayout();
  }

  private notifyFocus() {
    send({ type: "pane.focus", paneId: this.paneId });
  }

  private scheduleReport = () => {
    if (this.rafId) return;
    this.rafId = requestAnimationFrame(() => {
      this.rafId = 0;
      this.reportLayout();
    });
  };

  private reportLayout() {
    if (!this.content.isConnected) return;
    const rect = this.content.getBoundingClientRect();
    // Round to avoid sub-pixel jitter triggering a host repaint loop.
    const x = Math.round(rect.left);
    const y = Math.round(rect.top);
    const w = Math.round(rect.width);
    const h = Math.round(rect.height);
    if (w <= 0 || h <= 0) return;
    if (
      x === this.lastRect.x && y === this.lastRect.y &&
      w === this.lastRect.w && h === this.lastRect.h
    ) return;
    this.lastRect = { x, y, w, h };
    send({
      type: "urlpane.layout",
      paneId: this.paneId,
      url: this.url,
      x, y, w, h,
    });
  }
}
