// One Pane = one xterm.js Terminal bound to one host-side ConPty by paneId.
// Owns the DOM (header + term host), the FitAddon, and the input/output
// plumbing that ships bytes to/from the host.

import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { Unicode11Addon } from "@xterm/addon-unicode11";

import { b64ToBytes, bytesToB64, send } from "./bridge.js";

const utf8 = new TextEncoder();

export class Pane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly nameEl: HTMLElement;
  private readonly termHost: HTMLElement;
  private readonly term: Terminal;
  private readonly fit: FitAddon;
  private resizeFrame = 0;
  private lastCols = -1;
  private lastRows = -1;
  private observer?: ResizeObserver;

  constructor(paneId: string, name: string) {
    this.paneId = paneId;

    this.element = document.createElement("div");
    this.element.className = "pane";
    this.element.dataset.paneId = paneId;

    // Per-pane header: name label + close X. No HwndHost stealing clicks
    // here -- the whole UI is one HWND so a plain DOM button just works.
    const header = document.createElement("div");
    header.className = "pane__header";

    this.nameEl = document.createElement("span");
    this.nameEl.className = "pane__name";
    this.nameEl.textContent = name;
    header.appendChild(this.nameEl);

    const close = document.createElement("button");
    close.type = "button";
    close.className = "pane__close";
    close.title = "Close pane";
    close.setAttribute("aria-label", "Close pane");
    close.textContent = "✕";
    close.addEventListener("click", (ev) => {
      ev.stopPropagation();
      send({ type: "pane.close", paneId: this.paneId });
    });
    header.appendChild(close);
    this.element.appendChild(header);

    this.termHost = document.createElement("div");
    this.termHost.className = "pane__term";
    this.element.appendChild(this.termHost);

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

    // Any focus inside the pane (clicking the terminal, header, or X) marks
    // it active host-side so keyboard shortcuts know which pane to act on.
    this.element.addEventListener("focusin", () => this.notifyFocus());
    this.element.addEventListener("mousedown", () => this.notifyFocus());
  }

  /** Attach to the DOM and start observing for size changes. term.open
   *  must see a measured element or its canvas comes out 0x0; defer to
   *  the next animation frame so CSS Grid has settled.
   *
   *  Multiple kickers fire reportResize() to make absolutely sure the
   *  host gets a real pane.resize for this pane id. The ResizeObserver
   *  is supposed to fire once on observe(), but in practice when a pane
   *  is created during a session swap the initial contentRect is zero
   *  -- our size guard drops that, and nothing else triggers a re-report,
   *  so the host never spawns the PTY and the pane sits there blank. */
  attach(host: HTMLElement) {
    host.appendChild(this.element);
    requestAnimationFrame(() => {
      try { this.term.open(this.termHost); }
      catch (err) { console.error("[pane] term.open failed:", err); return; }
      this.observer = new ResizeObserver(() => this.reportResize());
      this.observer.observe(this.termHost);
      this.reportResize();
      requestAnimationFrame(() => this.reportResize());
      setTimeout(() => this.reportResize(), 30);
    });
  }

  dispose() {
    this.observer?.disconnect();
    this.observer = undefined;
    try { this.term.dispose(); } catch { /* ignore */ }
    this.element.remove();
  }

  feed(b64: string) {
    this.term.write(b64ToBytes(b64));
  }

  notifyExit(code: number) {
    this.term.writeln(`\r\n\x1b[2m[shell exited with code ${code}]\x1b[0m`);
  }

  focus() {
    this.term.focus();
  }

  setActive(active: boolean) {
    this.element.classList.toggle("pane--active", active);
  }

  setName(name: string) {
    this.nameEl.textContent = name;
  }

  /** Force a fresh fit + resize report. Used when the pane is reattached
   *  to a different DOM parent (e.g. after a split): the ResizeObserver
   *  fires inconsistently across browsers when an element changes parent
   *  but its content rect computes "the same", so the host never hears
   *  about the new dimensions and PowerShell keeps writing at the old
   *  cols/rows -- output overflows offscreen, pane looks blank. */
  forceRefit() {
    this.lastCols = -1;
    this.lastRows = -1;
    // Two frames: one for layout to settle, one for the report.
    requestAnimationFrame(() =>
      requestAnimationFrame(() => this.reportResize())
    );
  }

  private notifyFocus() {
    send({ type: "pane.focus", paneId: this.paneId });
  }

  private reportResize() {
    if (this.resizeFrame) return;
    this.resizeFrame = requestAnimationFrame(() => {
      this.resizeFrame = 0;
      try { this.fit.fit(); } catch { return; }
      const { cols, rows } = this.term;
      // Don't ship zero/tiny sizes -- the host's lazy-spawn gate ignores
      // them, but we'd waste a round trip and a re-render. The
      // ResizeObserver will fire again when CSS finishes laying us out.
      if (cols < 5 || rows < 3) return;
      if (cols === this.lastCols && rows === this.lastRows) return;
      this.lastCols = cols;
      this.lastRows = rows;
      send({ type: "pane.resize", paneId: this.paneId, cols, rows });
    });
  }
}
