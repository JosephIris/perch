# cmux-win design bible

This is the long-form companion to `CLAUDE.md`. CLAUDE.md is the
**constitution** — short, rule-shaped, "what we do and don't do." This
file is the **bible** — what those rules MEAN, the vocabulary you need to
talk about them, and concrete examples of the decisions behind them.

If the constitution and the bible disagree, the constitution wins. The
bible is meant to be edited as we learn; the constitution moves rarely.

---

## 1. Vocabulary

These are the words we use when discussing chrome. Using the right word
prevents the "I kind of want it to feel like…" loop.

### Surface tone

The dominant fill color of a region (sidebar, panel, dialog).

- **Translucent** — uses Mica/acrylic; the OS desktop bleeds through.
  Windows Terminal default. Looks "Win11-native" but the cast changes
  with the user's wallpaper, which can read as cold/blue.
- **Opaque** — a flat painted color. VS Code, Claude desktop, Slack.
  Predictable, calm, no wallpaper interaction.

cmux-win uses **opaque** for both the sidebar and the workspace pane,
with a deliberate **tonal stagger** between them:

- Sidebar (`--color-sidebar-surface: #181818`) is the **recessed** tone
- Pane / terminal (`--color-terminal-bg: #1f1f1f`) is the **lifted** tone

Sidebar darker, pane lighter. Differential is ~7 RGB units — small
enough to feel calm, large enough to read as "two surfaces, not one." The
same staircase shows up in Claude desktop, VS Code, and Files: the
navigation surface recedes, the content surface comes forward. Matching
both to the same tone *erases the staircase* and the app feels like a
flat sheet with regions drawn on it instead of layered surfaces.

Mica still composes through the title bar (the WPF chrome stays
translucent) so the OS still shows personality at the very top.

#### When the staircase goes the other way

Some apps invert this (sidebar lighter, content darker — Slack does it
in light mode). In dark mode the recessed-sidebar pattern is the
near-universal default; don't fight it without a strong reason.

### Hairline

A 1px border (usually `rgba(255,255,255,0.04)` over dark). Read as a
*separator hint*, not a divider. Differs from a **divider line**, which
is heavier (1px at 0.14+ opacity, or 2px). We default to hairlines and
escalate only when "grouping by space" can't carry the structure.

### Pill

The rounded rectangle background that marks a list-row state
(`border-radius: 6px` for sidebar rows, `8px` for cards). When you see
"selected pill" or "hover pill," that's this. The pill itself is
**neutral** — its color comes from the subtle-secondary / subtle-tertiary
tokens, not an accent. See [§ 4 Selection / hover / focus](#4-selection-hover-focus).

### Density / rhythm

How tight rows pack vertically. Three usable settings:

- **Tight** — 6px row padding, 2px row gap. Claude / Slack / Discord
  conversation lists. Right when there will be 10+ items.
- **Medium** — 8px / 8px. WPF-UI default. Right when each row carries
  multiple lines or chips.
- **Roomy** — 12px / 12px. Win11 Settings, Files app. Right when each
  row is conceptually a whole section (e.g. a settings card).

cmux-win sidebar is **tight**. The pane header and toast use medium.

### Selection / hover / focus

Three distinct states. **Don't conflate them.**

- **Hover** — pointer is over the element. Transient. Lightest weight.
  In our tokens: `--color-subtle-secondary` (0.04 alpha).
- **Selected** — element is the user's current pick (active session,
  selected setting). Persistent. Stronger than hover but still neutral.
  In our tokens: `--color-subtle-tertiary` (0.08 alpha).
- **Focus** — keyboard focus ring. Always 1px accent, never glowing.
  Lives independently of selection: a selected row can lose focus when
  the user clicks into the workspace.

The visual order should be `hover < selected < focus`. If a focused
element doesn't look distinct from a selected one, focus is broken.

### Optical sizing

Variable / OpenType fonts ship multiple cuts for different sizes (Segoe
UI Variable's Small / Text / Display, Inter doesn't but has a single
size-agnostic cut tuned for chrome). Using the right cut at small sizes
prevents the "looks bold even at 400" problem.

We use Inter Variable everywhere in chrome; the Segoe UI Variable cuts
remain in the fallback chain (`--font-text`, `--font-display`,
`--font-small`) so the app degrades gracefully if the bundled font fails
to load.

### Mica / acrylic / backdrop

- **Mica** — Win11's static, low-blur desktop tint. Applied to the WPF
  window via `WindowBackdropType="Mica"`. Doesn't show up in our
  `PrintWindow`-based screenshots; trust the running window.
- **Acrylic** — animated, higher-blur, more "frosted." Used on the
  toast surface and shortcut-hint overlay via
  `backdrop-filter: blur(40px) saturate(180%)`.
- **Backdrop** — generic term for whatever is composited behind a
  translucent surface. Mica and acrylic are two specific kinds of
  backdrop.

### Affordance

Visual hint that something is interactive. Examples:

- The close `✕` on a session row appears (`opacity: 0 → 1`) on hover —
  hover *is* the affordance; the icon's permanent absence keeps the row
  calm.
- The sidebar-toggle icon is **always** visible because the rail-only
  state needs to know how to get back.

---

## 2. Tokens

All chrome styling reads from CSS variables defined in
`src/web/src/tokens.css`. Never hardcode hex, px, or seconds in a
component. If you need a value that isn't in tokens.css, **add it to
tokens.css first**, then consume it.

### What's in tokens.css today

| Group | Variables | Use |
|---|---|---|
| Spacing | `--sp-4` … `--sp-48` | All paddings/gaps. 8px grid. |
| Radii | `--r-input` (4), `--r-pill` (6), `--r-card` (8), `--r-window` (12) | Per-shape radii. |
| Type | `--font-text`, `--font-display`, `--font-small`, `--font-mono` | Font stacks (Inter first). |
| Type sizes | `--type-caption-size` (12), `--type-body-size` (14), `--type-subtitle-size` (16), `--type-title-size` (20), `--type-large-size` (28) | The five chrome sizes. |
| Surfaces | `--color-sidebar-surface`, `--color-layer`, `--color-layer-alt` | Opaque region fills. |
| Selection | `--color-subtle-secondary` (hover), `--color-subtle-tertiary` (selected) | The two pill states. |
| Strokes | `--color-stroke`, `--color-stroke-strong`, `--color-sidebar-stroke` | Hairlines and dividers. |
| Text | `--color-text-primary` (0.92), `-secondary` (0.72), `-tertiary` (0.50), `-disabled` (0.34) | Four-tier text hierarchy. |
| Accent | `--color-accent`, `--color-accent-strong`, `--color-accent-soft` | Sparingly, for the active-pane border / kbd hint. |
| Status | `--color-success`, `--color-caution`, `--color-error` | Notify / toast tints. |
| State dots | `--color-state-idle/working/waiting/done/error` | Sidebar status dots. |
| Motion | `--ease-entry`, `--ease-exit`, `--dur-fast` (150ms), `--dur-normal` (200ms) | All transitions. |

### When to add a new token

Add one when you find yourself writing the same literal in two places, or
when a value carries semantic meaning ("this isn't just `#1d1d1d`, it's
the sidebar surface tone"). Don't add a token for a one-off value used
exactly once with no clear semantic name — that's premature.

### When NOT to use a token

xterm.js (the terminal renderer) ships with its own opaque background
(`#0c0c0c`) because cell rendering needs predictable contrast. The
terminal cell grid bypasses our token system on purpose.

---

## 3. Sidebar pattern

The sidebar is the most-iterated surface in the app. Document the
decisions so we don't relitigate them.

### Structure (top → bottom)

```
┌──────────────────┐
│  [☰]             │ icon rail (toggle, future: search)
│  + New session   │ top action(s)
│                  │
│  RECENTS         │ section label
│  ● main · pwsh   │ session rows (single line)
│  ○ alt · wsl     │
│  ...             │
│                  │
│  ▣ cmux          │ identity / footer
│    main pwsh     │
└──────────────────┘
```

### Why this layout (not the previous one)

The earlier version put "New session" at the bottom as a primary button
and rendered each row as two lines (title + shell). That worked but was
busier than necessary — the sidebar competed visually with the workspace.

We switched to **Claude-style** for three reasons:
1. **Top-anchored primary action** matches what users expect from
   conversation/chat apps in 2026.
2. **Single-line rows** halve the vertical footprint, so the sidebar
   stays useful at 10–20 sessions.
3. **Identity row at the bottom** anchors the chrome — without it, the
   sidebar feels like a list that runs off the edge.

### Per-row anatomy

```
┌─────────────────────────────────────┐
│ ●  main · powershell             ✕  │  ← .session-item
│ │  └─ title (Inter 380)             │
│ └─ status dot (color = agent state) │
└─────────────────────────────────────┘
```

The shell suffix is **tertiary** color (50% white) — it's metadata, not
content. Branch + ports, when present, render on an optional second line
in the same color tier.

### Anti-patterns specific to the sidebar

- **Don't add a 3px accent stripe.** That's the Win11 nav-item pattern
  and it's right for primary nav (Settings, command palette). The
  sidebar is secondary nav (workspace is primary), so it stays quiet.
- **Don't bold the title.** Inter at weight 380 is the choice; anything
  ≥500 reads as a heading, not a list item.
- **Don't put live terminal output in the row.** Notifications and
  status come from agent IPC (`cmux notify`/`cmux status`), not from
  scraping the terminal buffer. See `docs/NOTIFICATIONS.md`.
- **Don't show the close `✕` permanently.** Hover-or-active reveal only.

---

## 4. Selection / hover / focus

(See [§ 1 Vocabulary](#1-vocabulary) for the definitions; this section is
the implementation.)

Each interactive surface should answer all three states. Reference table:

| Surface | Hover | Selected | Focus |
|---|---|---|---|
| Session row | `--color-subtle-secondary` pill | `--color-subtle-tertiary` pill | (current: relies on selected state; if we add real keyboard nav, add a 1px accent inset) |
| `sidebar__action` (New session) | `--color-subtle-secondary` pill | n/a (not a persistent selection) | inherits browser outline |
| `icon-button` (sidebar toggle) | `--color-subtle-secondary` + text-primary | n/a | inherits browser outline |
| Pane | n/a | 1px `--color-accent-soft` border | n/a (selected == focused, conceptually) |

The "selection-via-fill-only" pattern is the cmux-win signature. If you
ever feel tempted to add an accent stripe to the session row, look at
the Claude desktop reference in `design-loop/references/claude-desktop.png`
first.

---

## 5. Font policy

We bundle **Inter Variable** (rsms/inter v4.0, OFL-licensed) at
`src/web/fonts/InterVariable.woff2`. esbuild's `copyStatics()` copies it
into `wwwroot/fonts/` on every build; the WebView2 virtual host serves
it at `/fonts/InterVariable.woff2`.

### Why Inter (and not Segoe UI Variable)

- Inter is the closest free analog to Söhne (Claude's brand font) on
  Windows: humanist proportions, open apertures, ink density tuned for
  small UI sizes.
- Segoe UI Variable is more geometric / condensed, which is great in
  Win11 Settings but reads as institutional when stacked next to a
  conversation list.

### Variable axis usage

Inter Variable supports any weight in [100, 900]. We use:

- **380** for session titles and action labels. Sits between Light and
  Regular — visually matches Söhne 400.
- **400** for body, status sub-labels, identity name. Standard regular.
- **600** for `BodyStrong` (the few places we want assertion: kbd hints,
  active-pane label).

If you need a new weight, prefer a non-integer in the existing range
over adding a new family. Variable fonts cost the same at any weight.

### When to override the font

The terminal cell grid uses `--font-mono` (`Cascadia Mono` first). Don't
mix monospace into chrome and don't mix Inter into the terminal — the
clear separation is part of the visual language.

---

## 6. Anti-patterns (with examples)

These are mistakes we've made or seen and don't want to repeat. Each one
includes *why* it's bad — so when an edge case looks like it might be
the exception, you can judge whether the reason still applies.

### "Add accent everywhere"

Bad:

```css
.session-item--active {
  background: var(--color-accent);
  color: var(--color-text-on-accent);
}
```

Why: the active session is a *current pick*, not a call-to-action. A
filled-accent background reads as "click me!" — exactly the wrong signal
for an item the user already picked. Use the neutral pill instead.

### "Bold the title to make it stand out"

Bad:

```css
.session-item__title { font-weight: 700; }
```

Why: at 14px, weight 700 reads as a heading. Every row becomes a
heading; nothing stands out. The selected pill already provides
emphasis — let typography stay quiet.

### "Add a shadow to the selected pill"

Bad:

```css
.session-item--active {
  background: var(--color-subtle-tertiary);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
}
```

Why: shadows imply elevation (the surface is *closer to the user*). A
selected list row isn't elevated — it's just marked. Use either a
border *or* a fill *or* a shadow, never two.

### "Just hardcode the color, it's only one place"

Bad:

```css
.foo { background: #1f1f1f; }
```

Why: today it's one place, in six months it's seven places and the
theme switch is impossible. Add a token first
(`--color-sidebar-surface`), then use it. Future-you will thank you.

### "We don't need a fallback font"

Bad:

```css
--font-text: "Inter Variable";
```

Why: WebView2 will sometimes fail to fetch the bundled font (cold start,
cache eviction, virtual host misconfig). With no fallback, the entire UI
renders in the browser's last-resort serif. Always include the Segoe
chain.

### "Wire the icon later"

Bad:

```html
<button class="icon-button" title="Search">🔍</button>
<!-- TODO: hook up search -->
```

Why: an interactive-looking control that does nothing is worse than no
control at all — users click it and lose trust. Either implement the
behavior or leave the icon out. We deferred search, back, and forward
from the title bar for exactly this reason.

---

## 7. Visual verification loop

The bible doesn't substitute for looking at the running window. Use this
loop when iterating on a chrome change:

1. `npm --prefix src/web run build` — rebuild the web bundle.
2. `dotnet build src/CmuxWin -c Debug` — copy bundle into bin output.
3. Launch the exe directly (
   `src/CmuxWin/bin/Debug/net8.0-windows/win10-x64/CmuxWin.exe`).
4. Wait ~3s for WebView2 to load, then run `scripts/screenshot.ps1`.
   It captures via `PrintWindow PW_RENDERFULLCONTENT`, so the WebView2
   contents come through (Mica does NOT — see below).
5. Open `design-loop/current.png` and
   `design-loop/references/claude-desktop.png` side-by-side.
6. Before changing anything, write 5–10 specific deltas — never "looks
   off." Examples: "title weight reads heavier than reference,"
   "sidebar surface is 1 step too dark," "row gap is 2px in ref, 4px
   here."
7. Make ONE focused change addressing the highest-impact delta.
8. Repeat from step 1.

### Mica caveat

`PrintWindow` captures the WPF rendering layer only. Mica (and any
DWM-composited backdrop) is applied by the desktop window manager
*after* the window renders, so it never appears in our captures — the
screenshot will look uniform-dark even when Mica is working correctly.
Trust the XAML for Mica correctness and eyeball the running window for
the bleed-through.

### Reference images

`design-loop/references/` is the set of images we compare against.
Today:

- `claude-desktop.png` — Claude desktop on Windows, the primary
  target for sidebar feel.
- `terminal-preview.png`, `files-app.png`, `devtoys.png`,
  `powertoys-settings.png` — Win11-native references for when the
  question is "how would Windows itself do this?"

---

## 8. Where things live

| Concept | File |
|---|---|
| Constitution (rules) | `CLAUDE.md` (repo root) |
| This bible (vocabulary, reasoning) | `docs/DESIGN-BIBLE.md` |
| Tokens | `src/web/src/tokens.css` |
| Global / shell styles | `src/web/src/style.css` |
| Sidebar markup | `src/web/index.html`, `src/web/src/sidebar.ts` |
| Workspace / pane | `src/web/src/workspace.ts`, `pane.ts` |
| WPF host (Mica, title bar) | `src/CmuxWin/MainWindow.xaml(.cs)` |
| Notification / toast surfaces | `docs/NOTIFICATIONS.md`, `src/web/src/toast.ts` |
| Renderer trade-offs | `docs/RENDERER_NOTES.md` |
| Parity vs upstream cmux | `docs/PARITY.md` |
