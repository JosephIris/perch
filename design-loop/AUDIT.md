# Step 3 audit — punch list

Generated against:
- `design-loop/current.png` (cmux dev build, dark Mica)
- `design-loop/references/terminal-preview.png`
- `design-loop/references/files-app.png`
- `design-loop/references/devtoys.png`
- `design-loop/references/powertoys-settings.png`

**What references share:**
- Mica visibly bleeds through chrome — sidebar + content surfaces are
  subtly translucent over desktop.
- Selected nav item is a muted gray pill + thin colored left stripe;
  never a vivid full-fill blue.
- 1px hairline border between sidebar and content, not a different solid
  background color.
- No per-pane heavy header strip (Windows Terminal puts tab labels in the
  titlebar, not on the panes).
- Accent color appears sparingly: thin left stripe on selected item,
  occasional affirmative button. Never as a 2px ring around an active pane.

**Punch list — what's wrong and where:**

| # | Issue | File / line | Property to change |
|---|---|---|---|
| 1 | **Mica killed everywhere.** Window has Mica + transparent bg (✅ MainWindow.xaml:14,17), but every child paints solid `LayerFillColorAltBrush` on top. | MainWindow.xaml:218 (sidebar Border), MainWindow.xaml:283 (status bar Border), MainWindow.xaml.cs:288 (pane header bar) | Set `Background="Transparent"` (or `SubtleFillColorTransparentBrush`) on those three places. |
| 2 | **Selected sidebar item is vivid #3B82F6.** Win11 Fluent uses a muted gray pill + thin accent stripe. | Themes/Tokens.xaml:112, MainWindow.xaml:175 (selected trigger), MainWindow.xaml:79,117,136 (text color triggers) | Replace bg with `SubtleFillColorSecondaryBrush`. Add a 3px-wide left-edge Border using `AccentFillColorDefaultBrush` shown only when selected. Revert text color trigger to default `TextFillColorPrimaryBrush`. |
| 3 | **Active pane border is vivid 2px #3B82F6 ring.** | MainWindow.xaml.cs:281 (`new Thickness(2)`), MainWindow.xaml.cs:443 (`SetActivePane`) | 1px theme `AccentFillColorDefaultBrush`, no glow. Or drop entirely. |
| 4 | **Per-pane header bar has filled background + underline** — heavy. | MainWindow.xaml.cs:288 (Background `LayerFillColorAltBrush`), MainWindow.xaml.cs:320-328 (underline Border) | Background `Transparent`. Remove underline Border. Keep label + close button. |
| 5 | **Hardcoded `#3B82F6` selection token violates CLAUDE.md "no hardcoded hex".** | Themes/Tokens.xaml:112-114 | Delete `Cmux.Selection.Color/Brush`. Use `AccentFillColorDefaultBrush` everywhere. |
| 6 | Title bar flat band. Auto-resolves after #1. | — | — |
| 7 | Sidebar–content hairline invisible against solid bg. Auto-resolves after #1. | MainWindow.xaml:219-220 | — |
| 8 | **Sidebar items have `Margin="0,0,0,2"` — too tight.** | MainWindow.xaml:150 (`SessionListItemStyle`) | `Margin="0,0,0,4"`. |
| 9 | Sidebar item icon is the wrong glyph. Cosmetic; defer. | MainWindow.xaml:57, MainWindow.xaml:206 | Skip in this pass. |
| 10 | Toast hardcoded `#E8E8E8`/`#1A1A1A` — deliberate exception for readability over dark terminal. ✅ Keep. | Themes/Tokens.xaml:124-126 | No change. |
| 11 | Status bar text already restrained. ✅ Background fixed by #1. | MainWindow.xaml:286-290 | — |
| 12 | Terminal HwndHost paints `0x0C0C0C` Campbell bg — needed for readability, no managed API to make it translucent. Mica through *surrounding chrome* is the achievable goal. | MainWindow.xaml.cs:386 | No change. |

**Order of attack (each fix → screenshot checkpoint):**
1. Drop solid backgrounds on sidebar / status bar / pane header (#1, #4) → biggest win, Mica appears.
2. Replace `Cmux.Selection.*` with `AccentFillColorDefaultBrush` everywhere; swap sidebar selected-item to gray-pill + left-stripe pattern (#2, #5).
3. Soften active pane border to 1px theme accent (#3).
4. Bump sidebar item spacing to 4px (#8).
5. Recapture, compare to references, iterate.

Continue with Step 4 of the loop (see `CONTINUE_PROMPT.md`). Don't change
anything without recapturing the screenshot after.
