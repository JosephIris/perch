// One Pane = one xterm.js Terminal bound to one host-side ConPty by paneId.
// Owns the DOM (header + term host), the FitAddon, and the input/output
// plumbing that ships bytes to/from the host.

import { Terminal } from "@xterm/xterm";
import type { ILinkProvider, ILink } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { Unicode11Addon } from "@xterm/addon-unicode11";
import { WebglAddon } from "@xterm/addon-webgl";

import { b64ToBytes, bytesToB64, send } from "./bridge.js";
import type { PaneTreeView } from "./bridge.js";
import { cachedClipboardText, copyText } from "./clipboard.js";
import { showLinkMenu } from "./link-menu.js";
import { URL_RE, findLinksInLine, htmlFileToUrl } from "./link-detect.js";
import { buildPaneHeader, applyChips, applyPorts, applyAgentBadge, applyModelChip } from "./pane-header.js";
import { buildPaneFooter, applyPaneFooter, type PaneFooter } from "./pane-footer.js";
import { createSetupOverlay, type SetupOverlay } from "./setup-overlay.js";
import { attachTooltip } from "./tooltip.js";
import { permissionDialogVisible, blockedDialogVisible } from "./perm-probe.js";

// The host appends ?nowebgl=1 when it re-navigates after a render-process
// crash, having pinned the WebGL renderer as the likely culprit. Honoring it
// means the recovery reload falls back to xterm's DOM renderer instead of
// re-crashing the GPU/render process on the same content. See
// MainWindow.OnWebViewProcessFailed.
const WEBGL_DISABLED = new URLSearchParams(location.search).has("nowebgl");

// URL_RE / findLinksInLine / htmlFileToUrl live in link-detect.ts (shared with
// the always-underline byte injection below and unit-tested there). URL_RE is
// re-used by injectUrlUnderlines; the xterm link provider uses findLinksInLine
// so it also surfaces local HTML files, not just web URLs.

const utf8 = new TextEncoder();

// Cursor show/hide accounting, across every pane. `churn` counts what the apps
// asked for; `hidden`/`shown` count what actually reached the screen after
// Pane.smoothCursorVisibility() collapsed a repaint's hide→show pairs. The
// difference IS the fix, so it's what the harness asserts on
// (`node scripts/cdp-eval.mjs "window.__perchCursor"` while codex works).
const cursorStats = { churn: 0, hidden: 0, shown: 0 };
(window as any).__perchCursor = cursorStats;

export const DEFAULT_FONT_SIZE = 13;
export const MIN_FONT_SIZE = 9;
export const MAX_FONT_SIZE = 32;

export class Pane {
  readonly paneId: string;
  readonly element: HTMLElement;
  private readonly nameEl: HTMLElement;
  private readonly stateDotEl: HTMLElement;
  private readonly stateLabelEl: HTMLElement;
  private readonly colorDotEl: HTMLElement;
  private readonly branchEl: HTMLElement;
  private readonly commitsEl: HTMLElement;
  private readonly portsEl: HTMLButtonElement;
  private readonly agentBadgeEl: HTMLElement;
  private readonly modelEl: HTMLButtonElement;
  private readonly footer: PaneFooter;
  // Boot cover shown while a Claude Code pane starts up (driven by pane.setup).
  private readonly setup: SetupOverlay;
  private setupActive = false;
  private readonly termHost: HTMLElement;
  private readonly term: Terminal;
  private readonly fit: FitAddon;
  private resizeFrame = 0;
  private lastCols = -1;
  private lastRows = -1;
  /** Last cwd this pane's shell reported via OSC 7. "" until the first prompt
   *  (or forever, in a shell that doesn't emit it) — the link provider treats
   *  empty as "can't resolve relative paths" rather than guessing. */
  private cwd = "";
  private observer?: ResizeObserver;
  private isActive = false;
  // Latest per-leaf view, kept so setActive() can re-evaluate focus-gated
  // chrome (the commit chip only shows on the active pane) without waiting
  // for the next state push.
  private lastLeaf?: Extract<PaneTreeView, { kind: "leaf" }>;
  // Agent-state reconciliation against the terminal buffer (see probeTick).
  private probeTimer = 0;
  private probeState = "";   // leaf state the streak counters accumulated under
  private probeStreak = 0;   // consecutive ticks agreeing with the pending report
  private probeSentAt = 0;
  // Full first-prompt text for the name tooltip; updated from each leaf view.
  private nameFull = "";

  constructor(paneId: string, name: string, fontSize: number = DEFAULT_FONT_SIZE) {
    this.paneId = paneId;

    this.element = document.createElement("div");
    this.element.className = "pane";
    this.element.dataset.paneId = paneId;

    // Per-pane header — color dot · name (double-click to rename) · state
    // dot + state word · close. The color dot is the persistent feature
    // tag the user assigns; the state dot reflects whatever perch status
    // last reported for THIS pane (not the whole session).
    const header = buildPaneHeader(paneId);
    this.element.appendChild(header.root);
    this.nameEl       = header.nameEl;
    this.stateDotEl   = header.stateDotEl;
    this.stateLabelEl = header.stateLabelEl;
    this.colorDotEl   = header.colorDotEl;
    this.branchEl     = header.branchEl;
    this.commitsEl    = header.commitsEl;
    this.portsEl      = header.portsEl;
    this.agentBadgeEl = header.agentBadgeEl;
    this.modelEl      = header.modelEl;
    this.nameEl.textContent = name;
    // Custom hover tooltip on the name: the full first-prompt the label was
    // cut from, or a rename hint when there's no prompt behind the name.
    attachTooltip(this.nameEl, () =>
      this.nameFull || "Double-click to rename"
    );

    this.termHost = document.createElement("div");
    this.termHost.className = "pane__term";
    this.element.appendChild(this.termHost);

    // Per-pane status bar at the bottom: live activity + work produced. Built
    // here, populated on every applyLeafView. Sits below the terminal so the
    // pane reads header · terminal · footer top-to-bottom.
    this.footer = buildPaneFooter();
    this.element.appendChild(this.footer.root);

    // Boot cover: appended last so it sits above the terminal within the pane's
    // stacking context. Hidden until the host sends pane.setup {show:true}.
    this.setup = createSetupOverlay();
    this.element.appendChild(this.setup.el);

    // Terminal theme. Background is --color-terminal-bg (#1f1f1f), one
    // step LIGHTER than --color-sidebar-surface (#181818) so the pane
    // reads as the "lifted" surface and the sidebar reads as "recessed"
    // navigation — see docs/DESIGN-BIBLE.md "Surface tone". xterm reads
    // these once at construction time; updating the CSS variables doesn't
    // change the canvas — bump explicitly via setOption if needed.
    this.term = new Terminal({
      fontFamily:
        '"Geist Mono Variable", "Cascadia Code", "Cascadia Mono", Consolas, monospace',
      fontSize: clampFontSize(fontSize),
      // Bump the variable-font weight axis. Geist Mono Variable supports
      // 100–900; 600 / 800 gives a denser, more present terminal that
      // pairs with Inter at 380 in the chrome (chrome reads light,
      // terminal reads weighty — clear hierarchy).
      fontWeight: 600,
      fontWeightBold: 800,
      // Terminal convention. 1.0 lets block characters (▀▄█) and
      // box-drawing characters (─│┌┘) tile flush across rows. Bumping
      // above 1.0 inserts a leading gap that those glyphs can't fill, so
      // TUIs (Claude Code, vim, htop) look broken. 1.0 is what Windows
      // Terminal / iTerm2 / Alacritty default to.
      lineHeight: 1.0,
      // Blink ONLY on the active pane — see setActive(). cursorBlink runs a
      // standing animation loop per Terminal; with several panes alive (the
      // exact case the user hit) the inactive panes' blink loops kept
      // toggling the WebGL cursor overlay while Claude's "thinking" spinner
      // streamed redraws, and the out-of-phase blink/redraw made the cursor
      // jump and ghost. Inactive panes draw no cursor anyway
      // (cursorInactiveStyle:"none"), so their blink loop is pure cost — we
      // start with it off and enable it only for whichever pane is focused.
      cursorBlink: false,
      cursorStyle: "block",
      // When the xterm Terminal isn't focused, draw nothing for the
      // cursor — eliminates the "two blinking cursors fighting for the
      // eye" effect that shows up the moment a second pane is alive.
      // workspace.setActive routes focus to the active pane and explicitly
      // blurs the previously-active one, so xterm's own focused/blurred
      // state stays in sync with which pane the user is acting on.
      cursorInactiveStyle: "none",
      allowProposedApi: true,
      scrollback: 10_000,
      // Tell xterm we're driving it from a ConPTY (Windows' pseudo-tty).
      // This switches xterm's reflow heuristics into ConPTY mode: lines
      // that wrap in the buffer are tracked with a per-line "this came in
      // as a continuation" flag, so a width change re-wraps coherently
      // instead of leaving fragments stranded in the scrollback. Without
      // this, resizing a pane mid-output (the common case: drag a split
      // boundary while Claude is streaming) corrupted the visible
      // scrollback. Supersedes the deprecated windowsMode flag.
      windowsPty: { backend: "conpty" },
      theme: {
        background: "#1f1f1f",
        foreground: "rgba(255, 255, 255, 0.92)",
        cursor: "#76B9ED",
        cursorAccent: "#1f1f1f",
        selectionBackground: "rgba(118, 185, 237, 0.32)",
      },
    });
    this.fit = new FitAddon();
    this.term.loadAddon(this.fit);
    this.term.loadAddon(new Unicode11Addon());
    this.term.unicode.activeVersion = "11";
    this.smoothCursorVisibility();

    // Custom link provider — always underlines URLs (decorations.underline:
    // true) and routes clicks to our link-action menu instead of opening
    // a new window. Replaces @xterm/addon-web-links entirely.
    this.term.registerLinkProvider(
      // cwd is read lazily (a getter, not a snapshot) — the provider outlives
      // any number of `cd`s, and a relative "out/report.html" must resolve
      // against where the shell is NOW, not where it was at pane creation.
      makeUrlLinkProvider(this.term, this.paneId, () => this.cwd)
    );

    // OSC 7 (cwd notification, file://hostname/path). PowerShell's prompt
    // hook injected by Shell.cs emits this on every prompt redraw. Forward
    // to the host so it can run git rev-parse and auto-fill the branch
    // chip without the agent having to call `perch meta --branch`.
    this.term.parser.registerOscHandler(7, (data) => {
      const cwd = parseOsc7Cwd(data);
      if (cwd) {
        // Kept locally too: the link provider resolves relative .html paths in
        // agent output ("design-loop/report.html") against it. Without a cwd
        // those tokens stay un-linked rather than turning into a bogus
        // absolute path.
        this.cwd = cwd;
        send({ type: "pane.cwd", paneId: this.paneId, cwd });
      }
      return true;
    });

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

    // Reserve the right button for paste even when the app (Claude Code, vim,
    // htop…) has mouse reporting on. Without this, xterm forwards the
    // right-button press/release to the PTY as a mouse report; a TUI that
    // implements its OWN right-click paste — Claude Code does — then pastes
    // too, so the clipboard lands twice (the bug the logs pinned: a button
    // report to CC immediately before our own contextmenu paste). Standard
    // terminals (Windows Terminal, iTerm2) never forward the right button for
    // this reason. Capture phase on termHost runs before xterm's own mouse
    // listeners (which sit on descendant elements), and stopImmediatePropagation
    // keeps the event from reaching them, so no report is sent. We deliberately
    // do NOT preventDefault here: the browser still fires contextmenu, which is
    // what drives the paste below. Left/middle buttons pass through untouched so
    // the app keeps normal mouse interaction.
    const reserveRightButton = (ev: MouseEvent) => {
      if (ev.button !== 2) return;
      ev.stopImmediatePropagation();
      // Propagation stops here, so the usual mousedown→focus path won't run for
      // a right-click; focus the pane explicitly so paste + subsequent typing
      // land in this terminal.
      this.term.focus();
      this.notifyFocus();
    };
    this.termHost.addEventListener("mousedown", reserveRightButton, true);
    this.termHost.addEventListener("mouseup", reserveRightButton, true);

    // Windows Terminal-style right-click: copy if there's a selection,
    // otherwise paste the clipboard. WebView2 grants navigator.clipboard
    // access for the perch.local virtual host without prompting.
    this.termHost.addEventListener("contextmenu", (ev) => {
      ev.preventDefault();
      this.handleRightClick();
    });

    // Copy-on-select. Whatever the user selects is copied to the clipboard on
    // mouse-up. This is the fix for "copy doesn't work under Claude Code":
    // when a TUI turns on application mouse reporting, xterm hands the drag to
    // the app and disables its own selection — UNLESS the user holds Shift,
    // which forces a local selection (xterm built-in). Either way, on mouse-up
    // we copy whatever got selected. The setTimeout lets xterm finalize the
    // selection first and still runs inside the click's transient-activation
    // window, so navigator.clipboard.writeText is permitted. The highlight is
    // left in place (we don't clear it), matching native terminals.
    this.termHost.addEventListener("mouseup", (ev) => {
      if (ev.button !== 0) return;
      setTimeout(() => {
        if (!this.term.hasSelection()) return;
        const text = this.term.getSelection();
        if (text) void copyText(text);
      }, 0);
    });
  }

  // NOTE: a journal row used to carry a "show in the terminal" button that
  // scrolled this pane to the matching line via the xterm search addon. It
  // could never work and has been removed. Claude Code takes the ALTERNATE
  // screen buffer, which has no scrollback at all — xterm holds only the ~30
  // currently-painted rows, and cc repaints those constantly. Measured on a
  // live pane: buffer.type === "alternate", buffer.length === rows,
  // baseY === 0. A prompt from two seconds earlier was already unfindable.
  // Anything that wants to search an agent pane's history has to read the
  // transcript (which is what the Inspector already does), not the terminal.

  // Re-entrancy guard for the readText() fallback below — set while a read is
  // in flight so a second right-click during the await can't queue a second
  // paste. The primary (cache) path is synchronous and needs no guard.
  private pasting = false;

  private async handleRightClick() {
    // Windows Terminal style: copy if there's a selection, else paste.
    if (this.term.hasSelection()) {
      const text = this.term.getSelection();
      if (text) await copyText(text);
      this.term.clearSelection();
      return;
    }
    // Paste from the host-pushed cache synchronously. navigator.clipboard
    // .readText() can stall in WebView2 long enough that the user right-clicks
    // again thinking it didn't register, queuing a second read and pasting
    // twice; reading the cache can't stall, so the double never starts.
    const cached = cachedClipboardText();
    if (cached) { this.term.paste(cached); return; }
    // Fallback: cache empty (before the host's first push, or an oversize /
    // non-text clipboard). Guard re-entrancy so a slow read here can't double
    // either.
    if (this.pasting) return;
    this.pasting = true;
    try {
      const text = await navigator.clipboard.readText();
      if (text) this.term.paste(text);
    } catch (err) {
      console.error("[pane] clipboard read failed:", err);
    } finally {
      this.pasting = false;
    }
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
      // Switch to the WebGL renderer AFTER the canvas is mounted. WebGL's
      // glyph atlas extends block / Powerline characters across the full
      // cell height, which the default canvas renderer does not — without
      // this, TUIs like Claude Code, vim, htop show faint horizontal
      // stripes through ▀▄█ block characters because the rasterized glyph
      // is the font's em size (smaller than the cell). Wrapped in
      // try/catch because some WebView2 builds fail WebGL context
      // creation and we want a working fallback.
      if (!WEBGL_DISABLED) {
        try {
          const webgl = new WebglAddon();
          webgl.onContextLoss(() => webgl.dispose());
          this.term.loadAddon(webgl);
        } catch (err) {
          console.warn("[pane] WebGL renderer unavailable, using canvas:", err);
        }
      }
      this.observer = new ResizeObserver(() => this.reportResize());
      this.observer.observe(this.termHost);
      this.reportResize();
      requestAnimationFrame(() => this.reportResize());
      setTimeout(() => this.reportResize(), 30);
    });
  }

  dispose() {
    this.stopProbe();
    this.observer?.disconnect();
    this.observer = undefined;
    try { this.term.dispose(); } catch { /* ignore */ }
    this.element.remove();
  }

  feed(b64: string) {
    // Inject ANSI SGR underline codes around URL matches so they render
    // persistently underlined (not just on hover). xterm's link
    // decorations are hover-only by design — to get always-underline we
    // pre-process the byte stream and let xterm's own SGR parser handle
    // the styling. The URL text in the buffer stays clean (codes are
    // OUTSIDE the URL), so the link provider's regex still matches for
    // clicks. URLs split across feed() chunks are emitted unstyled —
    // acceptable for v1, real-world shells write a URL in one chunk.
    const bytes = b64ToBytes(b64);
    const n = bytes.length;
    // Ack the ORIGINAL byte count once xterm has drained this write. The
    // host uses the ack to release PTY backpressure (see ConPty flow
    // control); acking only after the write callback fires is what bounds
    // the renderer's buffer under a fast producer. We report `n` (pre-
    // underline-injection) to match the byte count the host sent.
    this.term.write(injectUrlUnderlines(bytes), () => {
      send({ type: "pane.ack", paneId: this.paneId, bytes: n });
    });
  }

  notifyExit(code: number) {
    this.term.writeln(`\r\n\x1b[2m[shell exited with code ${code}]\x1b[0m`);
  }

  focus() {
    // While the boot cover is up, park focus on it so keystrokes can't reach cc.
    if (this.setupActive) { this.setup.el.focus(); return; }
    this.term.focus();
  }

  /** Host-driven boot cover (pane.setup). While shown, the terminal is blurred
   *  and focus parks on the overlay so nothing the user types reaches cc during
   *  boot; hiding restores terminal focus if this is the active pane. */
  showSetup(show: boolean) {
    if (show) {
      this.setup.show();
      this.setupActive = true;
      this.term.blur();
      this.setup.el.focus();
    } else {
      this.setup.hide();
      this.setupActive = false;
      if (this.isActive) this.term.focus();
    }
  }

  /** Stop a frame-oriented TUI from strobing the cursor.
   *
   * Codex draws its whole screen every frame, and each frame is bracketed with
   * "hide the cursor" (CSI ?25l) … paint … "show the cursor" (CSI ?25h). While
   * it works that is 20–60 hide/show pairs a second, and xterm honours every
   * one of them, so the block cursor strobes — the "blinking like crazy" the
   * owner hit the first time he ran codex in a pane. (Claude Code doesn't do
   * this: it keeps the cursor hidden for a whole turn.) Nothing is wrong with
   * either app; a real terminal's frame pacing just hides the churn and a
   * DOM/WebGL renderer doesn't.
   *
   * So: a hide is DELAYED by {@link HIDE_SETTLE_MS}, and a show arriving
   * before it lands cancels it. A repaint's hide→show pair (sub-millisecond)
   * therefore changes nothing on screen, while a genuine hide — a TUI that
   * really wants no cursor — still takes effect, a sixth of a second later
   * than it asked. Only the exact sequence `CSI ? 25 h/l` is touched; every
   * other private mode passes straight through.
   *
   * The replay counters are what let us hand a sequence back to xterm's own
   * handler without our handler eating it again. A same-flush collision with a
   * genuine app sequence is harmless: the two are byte-identical, so whichever
   * one is treated as the replay, the resulting cursor state is the same.
   */
  private smoothCursorVisibility() {
    const HIDE_SETTLE_MS = 150;
    let pendingHide = 0;
    let replayHide = 0;
    let replayShow = 0;
    let shown = true;

    const pass = (visible: boolean) => {
      if (visible) replayShow++; else replayHide++;
      shown = visible;
      this.term.write(visible ? "\x1b[?25h" : "\x1b[?25l");
    };
    // Exactly `CSI ? 25 <final>` — a multi-parameter private-mode set (rare,
    // but legal) is left entirely alone.
    const isCursorMode = (params: (number | number[])[]) =>
      params.length === 1 && params[0] === 25;

    this.term.parser.registerCsiHandler({ prefix: "?", final: "l" }, (params) => {
      if (!isCursorMode(params)) return false;
      if (replayHide > 0) { replayHide--; return false; }
      cursorStats.churn++;
      if (!pendingHide)
        pendingHide = window.setTimeout(() => {
          pendingHide = 0;
          if (shown) { cursorStats.hidden++; pass(false); }
        }, HIDE_SETTLE_MS);
      return true;
    });

    this.term.parser.registerCsiHandler({ prefix: "?", final: "h" }, (params) => {
      if (!isCursorMode(params)) return false;
      if (replayShow > 0) { replayShow--; return false; }
      cursorStats.churn++;
      if (pendingHide) { clearTimeout(pendingHide); pendingHide = 0; }
      if (shown) return true;          // already visible — the strobe's other half
      cursorStats.shown++;
      pass(true);
      return true;
    });
  }

  setActive(active: boolean) {
    this.isActive = active;
    this.element.classList.toggle("pane--active", active);
    // Drive xterm's own focused state in lockstep with whether we're the
    // active pane. Without this, every Terminal a user has clicked stays
    // "focused" from xterm's perspective and renders its own blinking
    // cursor — with cursorInactiveStyle="none" this blur() is what makes
    // inactive panes go cursor-free instead of double-blinking.
    if (active && this.setupActive) this.setup.el.focus();
    else if (active) this.term.focus();
    else             this.term.blur();
    // Only the focused pane runs a cursor-blink loop (see the constructor
    // note) — keeps the multi-pane cursor calm during heavy output.
    this.term.options.cursorBlink = active;
    // The commit chip and the footer's git stats are focus-gated; re-evaluate
    // now that active changed without waiting for the next state push.
    if (this.lastLeaf) {
      applyChips(this.branchEl, this.commitsEl, this.lastLeaf, active);
      applyPaneFooter(this.footer, this.lastLeaf, active);
    }
  }

  setName(name: string) {
    this.nameEl.textContent = name;
  }

  /** Push the latest per-leaf state into the header. Called from
   *  workspace.applyState on every render — must be idempotent and cheap. */
  applyLeafView(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    this.lastLeaf = leaf;
    this.nameFull = leaf.nameFull ?? "";
    this.nameEl.textContent = leaf.name;
    this.stateDotEl.dataset.state = leaf.agentState;
    // Header badge word. A finished turn ("done") reads as "idle" — calm, your
    // move, no rush; a dormant shell ("idle") shows nothing. Everything else
    // shows its raw state name.
    this.stateLabelEl.textContent =
      leaf.agentState === "idle" ? "" :
      leaf.agentState === "done" ? "idle" :
      leaf.agentState;
    this.colorDotEl.dataset.color = String(leaf.colorIndex);
    // data-color on the pane element drives the CSS rule that tints
    // .pane__name text in the same color as the dot — features pop
    // visually distinct without the user reading the name.
    this.element.dataset.color = String(leaf.colorIndex);
    // data-state on the pane element drives the attention-pulse border
    // animation when the agent reports "waiting" (typically: needs
    // permission). The CSS pulses border-color, no shadow — see the
    // [data-state="waiting"] rule in style.css.
    this.element.dataset.state = leaf.agentState;
    applyChips(this.branchEl, this.commitsEl, leaf, this.isActive);
    applyPorts(this.portsEl, leaf);
    applyAgentBadge(this.agentBadgeEl, leaf.agentType);
    applyModelChip(this.modelEl, leaf.agentType, leaf.model);
    applyPaneFooter(this.footer, leaf, this.isActive);
    this.syncProbe(leaf);
  }

  // ---- Agent-state reconciliation against the buffer -------------------------
  // The host's agent state is edge-triggered off cc hooks, and several edges
  // simply don't exist: a permission prompt dismissed without a hook (Esc
  // aborts the turn silently; a denial resumes without the tool; an approved
  // slow tool reports only at completion) pins a pane on red "permission" —
  // and the mirror image, a question/plan dialog whose notification was lost,
  // sits silent until the watchdog calls it "done" and a BLOCKED agent reads
  // calm. The buffer can't go stale the way edges can, so while the pane is in
  // a reconcilable state we peek at the bottom of it every couple of seconds
  // and report disagreements; the HOST decides (it knows which states were
  // inferred vs hook-asserted). Detection lives in perm-probe.ts (pure,
  // unit-tested). Three watches:
  //   permission → dialog gone       → report; host demotes to working(inferred)
  //   done       → blocked dialog up → report (inactive panes only — an ACTIVE
  //                pane's ❯ menu is usually the user driving /model); host
  //                promotes to waiting ONLY if its done was itself inferred
  //   waiting    → dialog gone       → report; host unwinds ONLY its own
  //                inferred promotion, never a hook-raised waiting (whose
  //                dialog, e.g. MCP elicitation, may not match cc's markers)
  // CLAUDE PANES ONLY: the markers are cc's chrome. Another agent that set
  // `perch status permission` via the CLI draws its own prompt UI — probing
  // it would demote a genuinely blocked agent.

  private syncProbe(leaf: Extract<PaneTreeView, { kind: "leaf" }>) {
    const eligible =
      leaf.agentType === "claude" &&
      (leaf.agentState === "permission" ||
        leaf.agentState === "waiting" ||
        leaf.agentState === "done");
    if (!eligible) {
      this.stopProbe();
      return;
    }
    if (leaf.agentState !== this.probeState) {
      this.probeState = leaf.agentState;
      this.probeStreak = 0;
    }
    if (!this.probeTimer)
      this.probeTimer = window.setInterval(() => this.probeTick(), 2000);
  }

  private stopProbe() {
    if (this.probeTimer) {
      clearInterval(this.probeTimer);
      this.probeTimer = 0;
    }
    this.probeState = "";
    this.probeStreak = 0;
  }

  /** Last rows of the buffer. Dialogs render at the bottom of the CONTENT, so
   *  this is the whole story — no viewport math, immune to user scroll. */
  private bufferTail(): string {
    const buf = this.term.buffer.active;
    const end = buf.length;
    let tail = "";
    for (let i = Math.max(0, end - 40); i < end; i++)
      tail += (buf.getLine(i)?.translateToString(true) ?? "") + "\n";
    return tail;
  }

  private probeTick() {
    const state = this.lastLeaf?.agentState;
    const tail = this.bufferTail();

    // Which observation would contradict the current state?
    let report: { permissionVisible?: boolean; blockedVisible?: boolean } | null = null;
    if (state === "permission" && !permissionDialogVisible(tail)) {
      report = { permissionVisible: false };
    } else if (state === "waiting" && !blockedDialogVisible(tail)) {
      report = { blockedVisible: false };
    } else if (state === "done" && !this.isActive && blockedDialogVisible(tail)) {
      report = { blockedVisible: true };
    }
    if (!report) {
      this.probeStreak = 0;
      return;
    }
    // Two consecutive agreeing ticks before reporting — a single one could
    // straddle a mid-redraw frame. The resend gate covers the host push
    // round-trip (and a host that keeps the state on purpose isn't spammed).
    if (++this.probeStreak < 2) return;
    const now = Date.now();
    if (now - this.probeSentAt < 10_000) return;
    this.probeSentAt = now;
    send({ type: "pane.probe", paneId: this.paneId, ...report });
  }

  /** Bump the terminal font size by `delta` px, clamped to [9, 32].
   * Re-fits afterward so cols/rows reflect the new cell metrics.
   * Returns the resulting size (already clamped) so callers can ship it
   * to the host for persistence. */
  changeFontSize(delta: number): number {
    const next = clampFontSize((this.term.options.fontSize ?? DEFAULT_FONT_SIZE) + delta);
    this.setFontSize(next);
    return next;
  }

  /** Reset the terminal font size to the default. */
  resetFontSize(): number {
    this.setFontSize(DEFAULT_FONT_SIZE);
    return DEFAULT_FONT_SIZE;
  }

  /** Apply a font size from the outside (e.g. on initial prefs push from
   *  host). Idempotent — skips the refit if the value already matches. */
  setFontSize(size: number) {
    const next = clampFontSize(size);
    if (this.term.options.fontSize === next) return;
    this.term.options.fontSize = next;
    this.forceRefit();
  }

  getFontSize(): number {
    return this.term.options.fontSize ?? DEFAULT_FONT_SIZE;
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

function clampFontSize(n: number): number {
  if (!Number.isFinite(n)) return DEFAULT_FONT_SIZE;
  return Math.max(MIN_FONT_SIZE, Math.min(MAX_FONT_SIZE, Math.round(n)));
}

// ---- OSC 7 parser ----------------------------------------------------------
// Input form: "file://<host>/<path>" — we want the local path. Spec says
// the host is the originating machine; we ignore it (we're not crossing
// machines). Path is percent-decoded. On Windows the path comes back as
// "/C:/Users/..." — strip the leading slash before / before drive letter.

function parseOsc7Cwd(data: string): string | null {
  const prefix = "file://";
  if (!data.startsWith(prefix)) return null;
  const slash = data.indexOf("/", prefix.length);
  if (slash < 0) return null;
  let path = data.slice(slash);
  try { path = decodeURIComponent(path); } catch { /* malformed */ }
  // Windows: "/C:/foo" → "C:/foo" → "C:\foo".
  if (/^\/[A-Za-z]:\//.test(path)) path = path.slice(1);
  path = path.replace(/\//g, "\\");
  return path;
}

// ---- URL underline injector -----------------------------------------------
// SGR sequences for terminal underline on/off, encoded as bytes.
const SGR_UNDERLINE_ON  = new Uint8Array([0x1b, 0x5b, 0x34, 0x6d]);       // ESC[4m
const SGR_UNDERLINE_OFF = new Uint8Array([0x1b, 0x5b, 0x32, 0x34, 0x6d]); // ESC[24m

// Hoisted out of injectUrlUnderlines so we don't recompile the regex on
// every PTY chunk (this runs per ~8 KB of output, per pane). The /g flag
// makes it stateful; we always run exec() to completion (until null), which
// resets lastIndex, so a single shared instance is safe across calls.
const URL_RE_G = new RegExp(URL_RE.source, "g");
// Latin-1 maps bytes 0x00–0xFF 1:1 to char codes, so decode() gives us the
// exact byte OFFSETS the regex needs without a per-char concat loop and
// without corrupting partial UTF-8 (we only use offsets, never the text).
const LATIN1 = new TextDecoder("latin1");

function injectUrlUnderlines(bytes: Uint8Array): Uint8Array {
  // Fast path: a URL needs a "://", so it must contain a ':' (0x3a). The
  // overwhelming majority of terminal chunks have none — skip the decode +
  // regex entirely for them. This keeps the hot output path cheap so it
  // can't stall the renderer thread that also services keystrokes.
  if (!bytes.includes(0x3a)) return bytes;

  const text = LATIN1.decode(bytes);
  URL_RE_G.lastIndex = 0;
  const matches: Array<{ start: number; end: number }> = [];
  let m: RegExpExecArray | null;
  while ((m = URL_RE_G.exec(text))) {
    matches.push({ start: m.index, end: m.index + m[0].length });
  }
  if (matches.length === 0) return bytes;
  // Build the output: original bytes interleaved with SGR escapes.
  const totalExtra =
    matches.length * (SGR_UNDERLINE_ON.length + SGR_UNDERLINE_OFF.length);
  const out = new Uint8Array(bytes.length + totalExtra);
  let read = 0;
  let write = 0;
  for (const match of matches) {
    // Copy bytes up to URL start
    out.set(bytes.subarray(read, match.start), write);
    write += match.start - read;
    // Insert ESC[4m
    out.set(SGR_UNDERLINE_ON, write);
    write += SGR_UNDERLINE_ON.length;
    // Copy URL bytes
    out.set(bytes.subarray(match.start, match.end), write);
    write += match.end - match.start;
    // Insert ESC[24m
    out.set(SGR_UNDERLINE_OFF, write);
    write += SGR_UNDERLINE_OFF.length;
    read = match.end;
  }
  // Copy remainder
  out.set(bytes.subarray(read), write);
  return out;
}

// ---- URL link provider -----------------------------------------------------
// Custom xterm link provider that always-underlines matched URLs and
// routes activation through our link-menu (instead of window.open).
// Replaces @xterm/addon-web-links — we keep the same regex, drop the
// dependency, and own decoration + activation behavior. Closures over the
// pane's own Terminal so split panes each get their own provider.

function makeUrlLinkProvider(
  term: Terminal,
  paneId: string,
  getCwd: () => string
): ILinkProvider {
  return {
    provideLinks(y: number, callback: (links: ILink[] | undefined) => void) {
      const buffer = term.buffer.active;
      const line = buffer.getLine(y - 1);
      if (!line) return callback(undefined);
      const text = line.translateToString(true);
      const out: ILink[] = [];
      for (const lk of findLinksInLine(text)) {
        const start = lk.start + 1;          // 1-based column
        const end = start + lk.text.length;  // exclusive
        // Web URLs open as-is; a local HTML file token becomes a file:// URL
        // the host / webview can navigate to. Both flow through the same menu.
        // A file token that can't be resolved to an absolute path (relative,
        // and the pane never reported a cwd) yields null — skip it rather than
        // underlining something that would open a blank pane.
        const menuUrl = lk.kind === "file" ? htmlFileToUrl(lk.text, getCwd()) : lk.text;
        if (!menuUrl) continue;
        out.push({
          range: { start: { x: start, y }, end: { x: end - 1, y } },
          text: lk.text,
          decorations: { underline: true, pointerCursor: true },
          activate: (ev: MouseEvent) => {
            const x = ev.clientX || window.innerWidth / 2;
            const y2 = ev.clientY || window.innerHeight / 2;
            // Wipe any selection xterm started on the underlying
            // mousedown — without this, moving the cursor over the menu
            // extends the selection across the terminal (the menu sits
            // above visually but xterm is still tracking).
            try { term.clearSelection(); } catch { /* defensive */ }
            // Blur the terminal so its mouse tracker stops responding to
            // moves while the menu is up. The first menu item gets focus
            // inside showLinkMenu; we just nudge xterm out of focus
            // proactively.
            try { (term.textarea as HTMLTextAreaElement | undefined)?.blur(); } catch {}
            showLinkMenu(menuUrl, x, y2, paneId);
            ev.preventDefault();
            ev.stopPropagation();
          },
        });
      }
      callback(out);
    },
  };
}
