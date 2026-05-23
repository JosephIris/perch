# cmux-win

[![Build](https://github.com/JosephIris/cmux-win/actions/workflows/build.yml/badge.svg)](https://github.com/JosephIris/cmux-win/actions/workflows/build.yml)

Native Win11 Fluent terminal app with sessions, nested pane splits, and per-session previews —
the missing Windows equivalent of [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux).

## Stack

- **WPF** (.NET 8) + [WPF-UI](https://github.com/lepoco/wpfui) for Win11 Fluent chrome
- [`EasyWindowsTerminalControl`](https://github.com/mitchcapper/EasyWindowsTerminalControl) wrapping
  the official `Microsoft.Terminal.Core` C++ renderer (same engine as Windows Terminal)
- ConPTY via `CI.Microsoft.Windows.Console.ConPTY`
- Mica backdrop, native window decorations, Job Object for child-process cleanup

No Chromium, no WebView2, no web tech.

## Features

- Vertical sidebar of sessions (rich cards: title, shell, live preview)
- Session lifecycle: add (with shell picker), rename (F2), drag-reorder, close
- Recursive pane splits (`Ctrl+Shift+D` right, `Ctrl+Shift+S` down) with nesting
- Active-pane indicator + focus-follow
- Per-pane header with close button (✕ or `Ctrl+Shift+W`)
- Typing `exit` closes the active pane (shell-PID polling)
- Live zoom across all panes (`Ctrl+=` / `Ctrl+-` / `Ctrl+0`)
- Settings dialog: font family, font size (with live preview), default shell
- Persistence: sessions, pane trees, window position survive restarts
- Status bar showing active `session · shell`

## Build

```pwsh
dotnet build src/CmuxWin -c Release
```

Or run directly:

```pwsh
dotnet run --project src/CmuxWin
```

Requires .NET 8 SDK on Windows 10+.

## CI

Pushes to `main` build on `windows-latest` and publish a self-contained artifact you can download
from the Actions tab. See `.github/workflows/build.yml`.

## Design constitution

`CLAUDE.md` at the repo root is the UI design contract — typography, spacing, color tokens, and
the verification loop. Future contributors (human or AI) read it before touching XAML.
