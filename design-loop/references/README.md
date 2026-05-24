# Reference screenshots

Drop reference shots here, named exactly as below so the audit/iteration steps
can pull each one by predictable filename. PNG preferred; 1× or 2× both fine.

| Filename                  | What to capture                                                      |
|---------------------------|----------------------------------------------------------------------|
| `windows-terminal.png`    | Windows Terminal Preview, dark theme, showing tab strip + titlebar integration + a couple of panes. Include 1–2 rows of content so we can compare terminal-pane chrome to ours. |
| `files-app.png`           | Files (files-community/Files), dark theme, sidebar visible, at least one folder card open. Look for: Mica background, sidebar density, item spacing. |
| `devtoys.png`             | DevToys, dark theme, with the left navigation list expanded and a tool open on the right. We're studying NavigationView pattern + nav-item density. |
| `vscode-insiders.png`     | VS Code Insiders on Win11, dark, with the editor + tabs + status bar visible. We want tab density, the muted status bar treatment, and the restraint on color. |

Optional but useful:
- `win11-settings.png` — Settings app for CardExpander / CardControl reference.
- `notepad-new.png` — new Notepad for FluentWindow + Mica + tab strip + restrained chrome.

Tips while you take the references:
- Don't capture full-screen; window screenshots only.
- Don't include other windows / desktop overlap.
- Keep DPI consistent if possible (these will be compared side-by-side).
