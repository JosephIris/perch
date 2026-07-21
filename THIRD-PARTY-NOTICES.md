# Third-party notices

Perch is distributed with the following third-party components. We're grateful
to their authors. Each is used under its own license; the full license texts are
available at the linked upstream projects.

## Bundled in the distributed app

These ship inside the installed application.

### Fonts

| Component | License | Notes |
|---|---|---|
| [Inter](https://github.com/rsms/inter) (Inter Variable) | SIL Open Font License 1.1 | UI/chrome typeface |
| [Geist Mono](https://github.com/vercel/geist-font) (Geist Mono Variable) | SIL Open Font License 1.1 | Monospace typeface |

> The SIL Open Font License requires that this attribution be retained. The
> fonts are used under the OFL and are not sold; the Reserved Font Names of
> each family are unchanged.

### Web renderer

| Component | License |
|---|---|
| [xterm.js](https://github.com/xtermjs/xterm.js) (`@xterm/xterm`) | MIT |
| `@xterm/addon-fit` | MIT |
| `@xterm/addon-web-links` | MIT |
| `@xterm/addon-webgl` | MIT |
| `@xterm/addon-unicode11` | MIT |
| `@xterm/addon-search` | MIT |

### .NET host

| Component | License |
|---|---|
| [WPF-UI](https://github.com/lepoco/wpfui) | MIT |
| [Velopack](https://github.com/velopack/velopack) | MIT |
| [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) | Proprietary (Microsoft SDK license); the Evergreen WebView2 Runtime is a Microsoft redistributable installed on the user's machine |
| [System.Management](https://github.com/dotnet/runtime) | MIT |

## Build-time only (not distributed)

Used to build Perch; not shipped in the installed app. Listed for completeness.

| Component | License |
|---|---|
| [esbuild](https://github.com/evanw/esbuild) | MIT |
| [TypeScript](https://github.com/microsoft/TypeScript) | Apache-2.0 |

---

If you believe a component is missing or mis-attributed here, please open an
issue or a pull request.
