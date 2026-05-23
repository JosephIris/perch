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
2. Capture the current window with the PowerShell screenshot script in
   /scripts/capture-window.ps1 (create it if missing — use
   System.Drawing.Graphics.CopyFromScreen against the main HWND).
3. Load BOTH the reference and the current screenshot. Before changing
   anything, write out 5–10 specific deltas (spacing, alignment, weight,
   radius, color, hierarchy). No vague observations like "looks off."
4. Make ONE focused change addressing the highest-impact delta.
5. Rebuild, recapture, recompare. Repeat.

Do not declare a UI task done without a fresh screenshot compared against
the reference.

## References to consult, not guess from

- Fluent Design System: https://learn.microsoft.com/windows/apps/design
- WPF-UI samples: https://github.com/lepoco/wpfui (Wpf.Ui.Demo.* projects)
- Segoe Fluent Icons cheatsheet:
  https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font

If you're unsure how a control should look or behave, open the WPF-UI demo
project's XAML for that control and match its patterns.
