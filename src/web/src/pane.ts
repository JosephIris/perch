// One Pane = one xterm.js Terminal bound to one host-side ConPty by paneId.
// The Pane owns the DOM element where xterm renders, the FitAddon, and the
// onData/onResize plumbing that ships bytes back to the host.

import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { Unicode11Addon } from "@xterm/addon-unicode11";

import { b64ToBytes, bytesToB64, send } from "./bridge.js";

const utf8 = new TextEncoder();

export class Pane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly term: Terminal;
  private readonly fit: FitAddon;
  private resizeFrame = 0;
  private lastCols = -1;
  private lastRows = -1;
  private observer?: ResizeObserver;

  constructor(paneId: string) {
    this.paneId = paneId;

    this.element = document.createElement("div");
    this.element.className = "pane";
    this.element.dataset.paneId = paneId;

    this.term = new Terminal({
      fontFamily: 'Cascadia Mono, "Cascadia Code", Consolas, monospace',
      fontSize: 13,
      lineHeight: 1.2,
      cursorBlink: true,
      cursorStyle: "block",
      allowProposedApi: true,
      scrollback: 10_000,
      theme: {
        background: "#0c0c0c",
        foreground: "#cdd6f4",
        cursor: "#cdd6f4",
        selectionBackground: "rgba(118, 185, 237, 0.3)",
      },
    });
    this.fit = new FitAddon();
    this.term.loadAddon(this.fit);
    this.term.loadAddon(new WebLinksAddon());
    this.term.loadAddon(new Unicode11Addon());
    this.term.unicode.activeVersion = "11";

    this.term.onData((data) => {
      send({
        type: "pane.in",
        paneId: this.paneId,
        b64: bytesToB64(utf8.encode(data)),
      });
    });

    this.element.addEventListener("focusin", () => {
      send({ type: "pane.focus", paneId: this.paneId });
    });
  }

  /** Attach to the DOM and start observing the host element for resizes.
   *  term.open needs the element to already have layout dimensions or its
   *  canvas comes out 0x0 and nothing renders even when bytes arrive.
   *  Defer to the next animation frame so CSS Grid has measured the cell,
   *  then explicitly fit + observe. */
  attach(host: HTMLElement) {
    host.appendChild(this.element);
    requestAnimationFrame(() => {
      try { this.term.open(this.element); }
      catch (err) { console.error("[pane] term.open failed:", err); return; }
      try { this.fit.fit(); } catch { /* element still 0x0 -- observer will retry */ }
      this.observer = new ResizeObserver(() => this.reportResize());
      this.observer.observe(this.element);
    });
  }

  /** Tear down xterm + DOM. Called when the pane is removed from the tree. */
  dispose() {
    this.observer?.disconnect();
    this.observer = undefined;
    try { this.term.dispose(); } catch { /* ignore */ }
    this.element.remove();
  }

  /** Hand bytes from the host PTY into xterm's VT parser. */
  feed(b64: string) {
    this.term.write(b64ToBytes(b64));
  }

  /** Render an inline banner when the backing PTY exits. */
  notifyExit(code: number) {
    this.term.writeln(`\r\n\x1b[2m[shell exited with code ${code}]\x1b[0m`);
  }

  /** Forward focus to the xterm input handler. */
  focus() {
    this.term.focus();
  }

  private reportResize() {
    if (this.resizeFrame) return;
    this.resizeFrame = requestAnimationFrame(() => {
      this.resizeFrame = 0;
      try { this.fit.fit(); } catch { return; }
      const { cols, rows } = this.term;
      if (cols === this.lastCols && rows === this.lastRows) return;
      this.lastCols = cols;
      this.lastRows = rows;
      send({ type: "pane.resize", paneId: this.paneId, cols, rows });
    });
  }
}
