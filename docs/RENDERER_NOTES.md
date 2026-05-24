# Terminal renderer — current choice and open questions

## What we use today

`Microsoft.Terminal.Wpf` (the CI fork of Microsoft's published WPF wrapper)
hosting Windows Terminal's native C++ render core via `EasyWindowsTerminalControl`.
This is the same renderer Windows Terminal ships. DirectWrite font rendering,
proper VT/ANSI support, fast, native, and visually consistent with the rest of
the OS — which matches the project's "Polished Fluent" identity in CLAUDE.md.

## What this renderer doesn't give us

The C++ core maintains its own cell grid, hyperlink ranges, OSC 8 state, and
selection geometry, but **none of it is projected to managed code**. The
public surface is essentially: connect a PTY, render bytes, expose
`GetSelectedText()`, swap themes. That's it.

In practice this means we cannot implement, without significant invention:

- Click-to-open URL detection (Tabby/iTerm style). No `GetCellAt(x, y)`,
  no rendered-buffer access, no link-provider extension point.
- OSC 8 hyperlink handling (`\x1b]8;;URL\x07text\x1b]8;;\x07`).
- In-buffer search ("Find in pane").
- Per-cell or per-range styling overlays (squiggle under errors, etc.).
- Programmatic selection from code (we can only *read* `GetSelectedText`).
- Re-rendering with ligatures, per-glyph fallback, image protocol, etc.

The URL feature in this repo works around the gap by using right-click on a
*mouse-selected* URL — conhost still owns selection, we just read the result
out of `GetSelectedText()` and decorate with our own context menu. Robust,
but it's the only renderer-feature in that category we'll get cheaply.

## When to revisit: switch to xterm.js + WebView2

If we ever want any of:

- Real click-on-URL with no select-first step,
- In-buffer search / find,
- Inline images (sixel, iTerm image protocol),
- A rich addon ecosystem (web-links, search, fit, unicode11, serialize,
  webgl, ligatures, etc.) without writing each one ourselves,
- OSC 8 hyperlinks rendered as underlines,

then the realistic alternative is **xterm.js hosted in a WebView2 control**.
That's how VS Code, Theia, Tabby, and most modern non-native terminals work.
xterm.js exposes `registerLinkProvider`, direct buffer access, and pixel-to-cell
math as first-class APIs.

### What that costs

The trade-off is real and partly conflicts with the project's design constitution:

- **Chromium subprocess.** WebView2 ships its own renderer process. Adds
  ~80MB resident, slower cold start, more memory per pane.
- **Font rendering.** Canvas/WebGL, not DirectWrite. Side-by-side with native
  Win11 chrome, the difference in text crispness is noticeable.
- **Airspace.** WebView2 is itself a HwndHost child — any WPF overlay near
  the terminal hits the same z-order problem we hit with the toast popup.
  Likely solvable per-element with Popups, but adds friction.
- **IPC overhead.** Input, output, focus, theme, font, and copy/paste all
  route through a JS↔C# bridge. Each one becomes a new surface to debug.
- **One-way door.** Once we're on xterm.js, coming back is a rewrite.
- **Identity.** CLAUDE.md says "NOT a web app. No Chromium, no React." This
  would soften that line.

### What it buys

- URL detection that actually works (point-click, OSC 8, custom matchers).
- Search-in-pane essentially free.
- A path to richer cell-level features over time.
- Visual control beyond what conhost permits — custom themes, per-line
  decorations, smooth scrolling, etc.

### A middle option: fork `Microsoft.Terminal.Wpf`

`microsoft/terminal` is open source. The C++ core already has a grid model,
a selection range, and hyperlink state. The managed wrapper doesn't project
any of it. A small patch that adds:

- `GetCellAt(int row, int col)`
- `(int row, int col) HitTest(int pixelX, int pixelY)`
- `IEnumerable<(int row, int col, int length, string url)> GetHyperlinks()`

...would unlock most of what we want without giving up DirectWrite or going
to a web stack. Cost: maintaining a fork of one DLL and rebuilding it when
upstream changes (which is rare — the package gets an update every few
months). This is the option to consider first if URL detection or in-buffer
search ever becomes load-bearing.

## Decision (as of this writing)

Stay with the current renderer. Use right-click-on-selection for URLs. If we
hit the wall with any of the features above, **try the fork before switching
renderers** — it preserves the project's identity at a fraction of the cost.
