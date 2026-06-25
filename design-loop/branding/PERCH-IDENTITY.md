# Perch - identity decisions

Rename + visual identity for this project (the Windows-native multi-session
terminal). Locked 2026-06-24.

## Positioning

- **Name:** Perch.
- **Tagline:** "A power tool for non-power users." (positioning / about / README
  line - **not** in-app copy.)
- **Why this, not "cmux":** "mux" reads as tmux / power-dev plumbing. This product
  is the inverse: a calm attention + context layer for non-devs running several
  agent projects (each tab = a project, each pane = a Claude Code / Codex session).
  cmux's stance is "a primitive, not a solution"; Perch is "a solution, for
  non-power users" - the tagline encodes that inversion.
- A long naming + collision sweep ruled out the obvious words: the dev-tool
  namespace is exhausted and the AI-agent-orchestration niche specifically is
  mobbed (Tendril, Herald x3, Aura, Vigilo, Wisp, Attune all taken; Tendril is a
  near-identical competitor). "Perch" had only weak/niche collisions and the
  metaphor matches the *interaction* (watch from a vantage, drop in when one
  project needs you), which is why it won.

## Hard rule: no invented product vocabulary

The bird/perch allegory lives **only** in the name, logo, and wordmark. Do **not**
push it into UI usage language - no "swoop" verb, do not rename "Needs you", etc.
Forced vocabulary is bad marketing; the app keeps speaking plainly
("2 projects need you", the `branch` chip, `+N commits`, select/open).

## Mascot - "Monocle Guy"

A perched songbird, side profile, facing right. **Pose 2 (absorbed):** head tipped
down ~15deg on a *drooping* branch, with a focused **brow**, an **accent monocle**,
and a monocle **string**. Reads as inquisitive / so absorbed he wouldn't notice if
he tipped off. Mascot scale only (hero, loading, 404, about, social) - never the
14px in-app mark.

- Asset: `perch-mascot.svg`

## App glyph / icon

The monocled eye **cropped straight off the mascot** (no redraw, same side view):
light head + brow + small dark side-eye + accent monocle ring + string. Earlier
attempt at a front-facing redrawn eye was wrong - it must be the bird's actual
side eye, lifted unchanged.

- `perch-glyph.svg` - full crop (head + brow + monocle + string). Use for the app
  icon (`.ico`).
- `perch-glyph-min.svg` - just the lens + eye, floated and enlarged. Use for the
  smallest sizes (the 14px sidebar mark); swap accents to `currentColor` for a
  fully monochrome mark if the cyan feels like too much in-app.

## Colors / tokens (from `src/web/src/tokens.css`)

- Accent (monocle + string): `#76B9ED` (`--color-accent`).
- Eye + brow: a dark tone (`#15233b` in the assets) - they read as dark detail on
  the light head. In the live app they were cut in the surface tone (`#181818`).
- Body / branch / wordmark: `currentColor` - set light on a dark/accent surface,
  grey/neutral in the sidebar. Wordmark font: **Inter Variable**, ~weight 500 for
  the logotype.
- App-icon tile in the mock: `linear-gradient(155deg,#20406b,#16243a,#11192a)`
  with the glyph in a light tone (`#edf4fd`) and a dark eye.

## Files (all in `design-loop/branding/`)

- `perch-identity.html` - token-accurate exploration page (real Inter + real
  tokens). Open in **Firefox** and refresh to iterate; `InterVariable.woff2` is
  copied next to it so the font loads under `file://`. The user refreshes the
  browser himself - **do not auto-launch** it.
- `perch-glyph.svg`, `perch-glyph-min.svg`, `perch-mascot.svg` - exported assets.

## Status - DONE

- App `.ico` built via `build_ico.py` (Pillow) = the cropped monocled-bird face,
  now at `src/Perch/Assets/perch.ico` (referenced by `Perch.csproj` ApplicationIcon
  + Resource, MainWindow `Icon`, and `installer/perch.iss`). Renaming the file
  (vs overwriting `cmux.ico`) forces MSBuild to re-embed it -> fixes the stale
  taskbar/exe icon.
- Sidebar footer glyph + `Perch` label wired in `src/web/index.html`.
- **Full internal purge done** (v1.7.0): repo + folders + namespaces renamed
  `cmux`/`CmuxWin` -> `Perch`; assembly `Perch.exe`, CLI `perch.exe`
  (`perch notify`/`status`/`meta`), namespace `Perch`/`PerchCli`, env vars
  `PERCH_*`, IPC pipe `\\.\pipe\perch\`, virtual host `perch.local`,
  `installer/perch.iss`, `tools/perch-cli`. Only the upstream `manaflow-ai/cmux`
  reference is intentionally kept. Verified with `npm run build` + `dotnet build`
  (0 errors; `Perch.exe` + `tools/perch.exe` produced).
