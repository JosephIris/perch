# Continuing the Fluent UI redesign

Paste the block below into Claude Code to resume the design loop. It's the
exact prompt that bootstrapped the screenshot harness, references, and audit.

State of play at the end of the last session:
- `scripts/screenshot.ps1` — PrintWindow-based capture, verified working.
- `design-loop/references/` — terminal-preview, files-app, devtoys,
  powertoys-settings dropped in.
- Step 3 audit complete; punch list of 12 items committed in the chat.
- Steps 4 and 5 (iterate + CLAUDE.md discipline) are next.

Open `design-loop/AUDIT.md` for the punch list, then paste the prompt below.

---

## The prompt (verbatim — paste this to continue)

We're working on cmux (WPF .NET 8, WPF-UI for Fluent, Microsoft.Terminal.Wpf renderer, WebView2 panes). The functionality is there but the UI looks like default dark WPF — not native Win11 Fluent. Help me fix this properly.

Before touching any XAML, set up a screenshot loop. Without it you're working blind and we'll go in circles.

## Step 1: Build the screenshot loop

Create `scripts/screenshot.ps1` that:
- Finds the CmuxWin process by name
- Uses PInvoke to PrintWindow (works even if occluded) to capture just that window
- Saves to `design-loop/current.png` plus a timestamped copy in `design-loop/history/`
- Prints the saved path

Test it works end-to-end before moving on.

## Step 2: Pull references

Create `design-loop/references/` and tell me which screenshots to drop in. I'll grab:
- Windows Terminal Preview (tab strip, titlebar integration)
- Files app (sidebar, Mica treatment)
- DevToys (left-nav tool pattern)
- VS Code Insiders Win11 (tab density, status bar restraint)

Don't proceed past this step until I confirm the references are in place.

## Step 3: Audit current state

Run the screenshot loop on the current build. Then for each issue, tell me which XAML file and which property is causing it. Specifically check:
- Is the root window `ui:FluentWindow` with `WindowBackdropType="Mica"`?
- Is `ExtendsContentIntoTitleBar="True"` set?
- Is there a solid Background brush anywhere in the visual tree above where Mica should show? (This is the #1 Mica-killer.)
- Is Microsoft.Terminal.Wpf's background transparent, or is it a black rectangle covering Mica?
- Are we using `ui:NavigationView` / `ui:ListView` / WPF-UI themed controls, or stock WPF controls?
- Are colors reading from `{DynamicResource}` theme tokens, or hardcoded hex?
- Corner radii: 8px on surfaces, 4px on controls, consistently?

Give me a punch list before changing anything.

## Step 4: Iterate with the loop closed

After EVERY visual change:
1. `Get-Process CmuxWin -EA SilentlyContinue | Stop-Process -Force`
2. `dotnet build -c Debug`
3. Launch the new build (background, non-blocking)
4. `Start-Sleep -Seconds 2`
5. `pwsh ./scripts/screenshot.ps1`
6. View `design-loop/current.png` and compare it against the relevant reference
7. Describe the gap in concrete terms (spacing, density, color, weight) and iterate
8. Do not declare a task "done" without a screenshot that shows it done

If a change doesn't visibly take effect in the screenshot, assume the change didn't land — don't assume it worked. Common causes: hardcoded brush overriding theme, wrong DynamicResource key, Background set somewhere up the tree.

## Step 5: Fluent discipline going forward

Add a `CLAUDE.md` section enforcing:
- Root window: `ui:FluentWindow` with Mica, never `Window`
- Never hardcode hex colors; use `{DynamicResource ApplicationBackgroundBrush}` etc. from WPF-UI
- Never set solid Background on containers that should let Mica through
- All chrome controls (tabs, nav, lists, buttons) come from WPF-UI namespace, not stock WPF
- Microsoft.Terminal.Wpf surface: transparent background so the terminal pane sits on Mica
- Corner radius: 8px surfaces, 4px controls, read from theme tokens not literals
- Status bar: low contrast, theme-token foregrounds, never bright saturated colors competing with content

## Important

- Do NOT install microsoft/win-dev-skills — that's WinUI 3 only and will give you wrong namespaces/APIs for WPF.
- If you're tempted to "just describe the fix" without running the screenshot loop, stop. The whole reason the previous attempt failed is iterating without seeing the result.
- Match implementation effort to the aesthetic: Fluent is restraint, not maximalism. The goal is for cmux to look like it ships with Windows 11, not to look like an AI made it pretty.

Start with Step 1. Don't skip ahead.
