# Perch

**A power tool for non-power users.**

[![Build](https://github.com/JosephIris/perch/actions/workflows/build.yml/badge.svg)](https://github.com/JosephIris/perch/actions/workflows/build.yml)

Native Win11 Fluent terminal app with sessions, nested pane splits, and per-session previews —
the missing Windows equivalent of [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux).

## Stack

- **WPF** (.NET 8) — a thin [WPF-UI](https://github.com/lepoco/wpfui)
  `FluentWindow` host: Win11 Mica backdrop, native window decorations, window
  lifetime, ConPTY processes, and the agent IPC layer.
- **WebView2 + [xterm.js](https://xtermjs.org)** render everything visible —
  the terminal panes (xterm's WebGL renderer) and the chrome (sidebar, tab
  strip, status bar, menus). The web bundle is served to WebView2 from an
  in-process virtual host (`https://perch.local/`).
- **Vanilla TypeScript + CSS**, bundled with
  [esbuild](https://esbuild.github.io) into `src/Perch/wwwroot/` — no UI
  framework, no CDN, fully offline.
- **ConPTY** (Windows pseudo-console) per pane, with a Job Object for
  child-process cleanup.

The previous all-WPF renderer (`Microsoft.Terminal.Wpf`) is preserved at tag
`wpf-final` / branch `wpf-archive`.

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
dotnet build src/Perch -c Release
```

Or run directly:

```pwsh
dotnet run --project src/Perch
```

Requires .NET 8 SDK on Windows 10+.

## CI

Pushes to `main` build on `windows-latest` and publish a self-contained artifact you can download
from the Actions tab. See `.github/workflows/build.yml`.

## Releases

Push a `vX.Y.Z` tag to cut a release. The build picks the tag as the version, produces both the
installer and the portable exe, and uploads them as assets on a new GitHub Release.

```pwsh
git tag v0.1.0
git push origin v0.1.0
```

Releases land at https://github.com/JosephIris/perch/releases.

## Design constitution

`CLAUDE.md` at the repo root is the UI design contract — typography, spacing, color tokens, and
the verification loop. Future contributors (human or AI) read it before touching XAML.
