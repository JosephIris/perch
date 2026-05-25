# UI Design Constitution — WPF Terminal App

You are working on a native WPF (.NET 8) application with Win11 Mica chrome,
native window decorations, and the Microsoft.Terminal.Core renderer. This is
NOT a web app. No Chromium, no React, no Tailwind. All UI is XAML.

## Target aesthetic

"Polished Fluent" — the same family as Windows Terminal, the Win11 Settings
app, new Notepad, and Visual Studio 2022. NOT generic Material, NOT macOS
mimicry, NOT 2015-era flat design. When in doubt, open Windows Terminal and
ask whether what you're building would feel at home next to it.

Reference quality bar: Windows Terminal, VS Code's Windows build, Files app
(files-community/Files on GitHub), the Win11 Settings app.

## Library

Use WPF-UI (lepoco/wpfui) as the control baseline. Prefer its controls over
stock WPF controls (`ui:Button` over `Button`, `ui:TextBox` over `TextBox`,
`ui:NavigationView`, `ui:ContentDialog`, etc.). Do not mix in MaterialDesign,
HandyControl, or other kits.

## Design tokens — ground truth

All spacing, type, color, and radius values MUST come from the central
ResourceDictionary (Themes/Tokens.xaml). Never hardcode literals in views.
If a token doesn't exist for what you need, add it to Tokens.xaml first,
then consume it. Tokens to respect:

- Spacing scale: 4, 8, 12, 16, 24, 32, 48 (no 5, 7, 13, 20)
- Corner radius: 4 (inputs), 8 (cards/buttons), 12 (windows/dialogs)
- Type: Segoe UI Variable, with the correct optical size for each role:
    - Caption 12px      → Segoe UI Variable Small,   weight 400
    - Body 14px         → Segoe UI Variable Text,    weight 400
    - BodyStrong 14px   → Segoe UI Variable Text,    weight 600
    - Subtitle 16px     → Segoe UI Variable Text,    weight 600
    - Title 20px        → Segoe UI Variable Display, weight 600
    - LargeTitle 28px   → Segoe UI Variable Display, weight 600
  Line-height 1.4 for body, 1.2 for titles. Letter-spacing 0 (don't tighten).
  Fallback chain: `Segoe UI Variable Text, Segoe UI, sans-serif`.
  Terminal surface uses Cascadia Mono (user-overridable in settings).
  Never use Inter, SF Pro, Calibri, or any non-Segoe family for app chrome.
  Set `TextOptions.TextFormattingMode="Ideal"` and `TextRenderingMode="ClearType"`
  on text-heavy containers.
- Color: consume `{DynamicResource}` from the WPF-UI theme dictionaries
  (ApplicationBackgroundBrush, TextFillColorPrimaryBrush, AccentFillColorDefaultBrush,
  etc.). Never hardcode hex.
- Elevation: prefer hairline 1px borders (ControlStrokeColorDefaultBrush) over
  drop shadows. Shadows only on flyouts and dialogs, and only via WPF-UI defaults.
- Motion: 150–200ms, Win11 easing (CubicBezier 0.0, 0.0, 0.0, 1.0 for entry).
  No bouncy springs. No animations over 250ms.

## Anti-patterns — do not produce these

- Gradient buttons or gradient backgrounds (except Mica/acrylic from the OS)
- Drop shadows on text
- Purple-to-pink or any "AI app" gradient
- Emoji in headings or button labels
- Fully filled secondary buttons (use subtle/outline variants)
- Three-color icons; stick to single-stroke Fluent System Icons or Segoe Fluent Icons
- Centered body text in panels
- Borders AND shadows on the same element
- Card-in-card-in-card nesting
- Title-case button labels ("Save Changes"); use sentence case ("Save changes")
- Placeholder text used as a label substitute

## Layout principles

- Generous whitespace. When unsure between two paddings, pick the larger.
- 8px grid for everything. 4px only inside dense controls.
- Left-align text in panels and lists. Center only for empty states and dialogs.
- Group related controls with spacing, not borders. Reach for a border only
  when grouping-by-space isn't enough.
- Settings pages: WPF-UI's `CardExpander` and `CardControl` patterns, one
  setting per row, label left, control right.

## When asked to add or change UI

Before writing XAML:
1. State which existing component/page this fits into, or argue for a new one.
2. List the tokens you'll consume. If any are missing, add them to Tokens.xaml first.
3. Name the WPF-UI controls you'll use.
4. If the change touches more than one view, describe the pattern so it stays
   consistent.

Then write the XAML. Keep code-behind minimal; prefer bindings and commands.

## Visual verification loop

When iterating against a reference screenshot:
1. Build and launch the app via `dotnet run` (or the existing launch script).
2. Capture the current window with `scripts/screenshot.ps1` — it uses
   `PrintWindow` with `PW_RENDERFULLCONTENT` so it captures the WPF render
   layer plus any hosted native HWND children (terminal, WebView2). Saves
   to `design-loop/current.png` and a timestamped copy in
   `design-loop/history/`.
3. Load BOTH the reference and the current screenshot. Before changing
   anything, write out 5–10 specific deltas (spacing, alignment, weight,
   radius, color, hierarchy). No vague observations like "looks off."
4. Make ONE focused change addressing the highest-impact delta.
5. Rebuild, recapture, recompare. Repeat.

Do not declare a UI task done without a fresh screenshot compared against
the reference.

**Important caveat — Mica won't show in screenshots.** `PrintWindow` captures
the WPF rendering layer only. Mica (and any DWM-composited backdrop) is
applied by the desktop window manager *after* the window renders, so it
never appears in our captures — the screenshot will look uniform-dark even
when Mica is working correctly. Trust the XAML for Mica correctness and
eyeball the running window for the bleed-through.

## Fluent discipline — non-negotiable rules

These were learned the hard way (each rule has a story behind it). Don't
regress them without a screenshot showing the new approach reads better.

### Mica must reach through chrome

- Root window is `ui:FluentWindow` with `WindowBackdropType="Mica"`,
  `ExtendsContentIntoTitleBar="True"`, `Background="Transparent"`. NEVER use
  stock `Window` — Mica won't apply.
- The #1 Mica-killer is a child element painting a solid `Background` over
  the area where Mica should bleed through. Sidebar containers, status bar,
  pane header gutters: all `Background="Transparent"` (or one of WPF-UI's
  translucent `*Transparent*Brush` tokens). Never use
  `LayerFillColorAltBrush` (or any solid token) on a container that should
  show OS chrome.
- Acceptable solid surface: the terminal HwndHost itself (conhost paints
  its own `0x0C0C0C` background for legibility — that's the only opaque
  rectangle in the visual tree). Surrounding chrome compensates by being
  transparent.

### No hardcoded hex colors

- All brushes come from `{DynamicResource ...}` against WPF-UI's theme
  dictionaries: `ApplicationBackgroundBrush`, `LayerFillColorDefaultBrush`,
  `SubtleFillColorSecondaryBrush`, `AccentFillColorDefaultBrush`,
  `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`,
  `TextFillColorTertiaryBrush`, `ControlStrokeColorDefaultBrush`, etc.
- A hardcoded hex literal anywhere in views/controls is a bug.
- Deliberate exception, narrowly documented in `Themes/Tokens.xaml`: the
  toast surface (`Cmux.Toast.*`) is intentionally light gray + near-black
  in BOTH themes because it overlays the dark terminal area where the
  WPF-UI translucent surfaces had no contrast. If you add another exception,
  document the reason in the same place.

### Selection / accent treatment

- Selected nav items: subtle gray pill (`SubtleFillColorTertiaryBrush` or
  similar) + a 3px-wide vertical accent stripe on the inner-left edge
  (`AccentFillColorDefaultBrush`), inset top/bottom so the pill's rounded
  corners still read. Never a vivid full-fill accent — that reads as a
  call-to-action button, not a selected state.
- Active pane / focus rings: 1px theme accent at most. Never 2px, never
  glowing.
- Text on selected items stays at the default foreground
  (`TextFillColorPrimaryBrush`/`TextFillColorSecondaryBrush`); do NOT
  switch to `TextOnAccentFillColor*` because the background isn't an
  accent fill anymore.

### Controls

- Chrome (buttons, lists, nav, menus, dialogs, dropdowns): WPF-UI
  namespace (`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`). Stock
  WPF controls only when WPF-UI has no equivalent.
- For icons inside a `ui:Button`, use a `ui:SymbolIcon` or `ui:FontIcon` as
  the Content — NOT a Unicode codepoint string + Button.FontFamily. The
  Button's content presenter doesn't reliably inherit FontFamily, so the
  glyph renders as zero-width tofu.
- Native `Microsoft.Terminal.Wpf.TerminalControl` surface stays as conhost
  renders it. We can't make the terminal cell grid translucent, can't
  decorate cells, can't hit-test clicks against URLs — see
  `docs/RENDERER_NOTES.md` for the trade-off and when to revisit.

### Sidebar pattern

- Each row: title (BodyStrong) + secondary label (Caption) + optional
  trailing label (Caption, tertiary brush). Pure data, no live tail of the
  terminal buffer. Modeled after cmux for macOS's
  `CmuxExtensionSidebarRenderRow`.
- Activity timestamp ("now" / "5m ago") is bumped from the
  `TermPTY.TerminalOutput` event (push), throttled to ~1Hz. Never
  `GetConsoleText()` — it copies the entire screen buffer on the UI thread
  and causes typing lag.
- Item spacing: 4px between cards. 2px reads cramped.

### Corner radii

- Surfaces / cards / windows: 8px (`Radius.Card`).
- Inputs (text boxes, dropdowns): 4px (`Radius.Input`).
- Window itself: 12px via `WindowCornerPreference="Round"` (Win11
  decides the exact value).
- Accent stripes / focus indicators: matched to the parent surface's radius
  divided by 2 if you want them rounded; or rectangle if hairline.

### Status bar

- Foreground: `TextFillColorSecondaryBrush` or tertiary. Never primary,
  never an accent color competing with content.
- Background: transparent (Mica reaches through) plus a 1px top hairline
  border (`ControlStrokeColorDefaultBrush`) as separator.
- Density: single short line. If you find yourself wanting two rows,
  the data belongs in the active pane or a flyout, not the status bar.

## References to consult, not guess from

- Fluent Design System: https://learn.microsoft.com/windows/apps/design
- WPF-UI samples: https://github.com/lepoco/wpfui (Wpf.Ui.Demo.* projects)
- Segoe Fluent Icons cheatsheet:
  https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font

If you're unsure how a control should look or behave, open the WPF-UI demo
project's XAML for that control and match its patterns.
