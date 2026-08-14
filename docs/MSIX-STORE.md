# Microsoft Store (MSIX) build

Perch ships to the Microsoft Store as an MSIX package. The Store signs the
package on ingestion, so SmartScreen never fires, Smart App Control never
blocks, and install is a one-click "Get". This is the primary channel for the
non-power-user segment; the Velopack `Setup.exe` on GitHub stays as the
power-user / sideload channel.

Windows Terminal ships to the Store exactly this way (a full-trust packaged
desktop app that spawns ConPTY), which is the existence proof that our
architecture packages cleanly.

## Why this works with our stack

Everything Perch persists already goes through `AppPaths.DataRoot`
(roaming AppData): the session store, settings, WebView2 user-data folder, and
the error log. Under MSIX those writes land in the *real* user AppData, not a
package-private copy: Windows 10 1903 dropped AppData redirection for packaged
desktop apps, so `%APPDATA%\perch` is one folder shared by every install.
Verified 2026-07-27 by installing the MSIX next to the Velopack build — the
packaged window listed the unpackaged install's real sessions and no
`LocalCache\Roaming\perch` was ever created.

Two consequences, one good and one that needed a fix:

- Switching channels is seamless. A user who installs from the Store after
  using the GitHub `Setup.exe` keeps every session, project and setting.
- Two installs running at once would race on `sessions.json`, last writer
  wins. `SingleInstance` (`src/Perch/SingleInstance.cs`) closes that: a launch
  whose data root is already claimed focuses the existing window and exits. The
  mutex is keyed on the resolved data root, so `PERCH_DATA_DIR`-isolated test
  instances still coexist with the real one.

Nothing writes next to the read-only install dir:
`wwwroot` and `tools/` are read/execute only, and the WebView2 user-data
folder is explicitly pointed at AppData (`MainWindow.xaml.cs`), which is the
single most common MSIX breakage already avoided.

`PERCH_DATA_DIR` still overrides the data root, so an isolated smoke test can
keep out of your real session store.

## Code that is Store-aware

- `PackagedRuntime.IsPackaged` (`src/Perch/PackagedRuntime.cs`) detects MSIX
  identity via `GetCurrentPackageFullName`.
- `UpdateService` stands down when packaged: `IsUpdatable` is false, `CheckAsync`
  never touches the GitHub feed, and `IsManagedExternally` is true so Settings
  can say "Updates are managed by the Microsoft Store" instead of offering a
  dead "Check now". The Store owns updates for this channel.

## One-time setup

1. In Partner Center: **Perch Workspace** -> Product management ->
   **Product identity**. Copy three values:
   - `Package/Identity/Name`               -> `Name`
   - `Package/Identity/Publisher`          -> `Publisher` (a `CN=...` string)
   - `Package/Properties/PublisherDisplayName` -> `PublisherDisplayName`
2. `cp packaging/identity.example.json packaging/identity.json` and paste them
   in. Keep `Version` 4-part with a trailing `.0` (the Store reserves the 4th
   part). `identity.json` is gitignored: the assigned identity is confidential
   to the account.

The reserved product name is **Perch Workspace** ("Perch" was taken). That
string is baked into the manifest's `DisplayName` and must match the
reservation exactly; the tile short name stays "Perch".

## Build

```powershell
# The package you upload to the Store (unsigned; the Store signs it):
powershell -ExecutionPolicy Bypass -File packaging/pack-msix.ps1

# Regenerate tile assets from the branding glyph (only if the logo changes):
powershell -ExecutionPolicy Bypass -File packaging/generate-assets.ps1
```

Output: `packaging/out/Perch_<version>.msix`. Upload that in Partner Center
under Packages. At submission you will be asked to justify the `runFullTrust`
restricted capability: "a terminal/agent workspace runs local developer tools
(shells, git, claude) on the user's behalf" is the honest, accepted answer.

## Local smoke test (before you ever submit)

The Store re-signs on ingestion, but to install on THIS machine you self-sign
and trust the cert:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/pack-msix.ps1 -Sign
# then, in an ELEVATED PowerShell (the script prints these):
Import-Certificate -FilePath packaging/out/PerchTest.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage packaging/out/Perch_<version>.msix
```

Launch from the Start tile and verify the three things package identity could
plausibly break:

1. **A pane opens a working shell** (ConPTY spawns under package identity).
2. **`claude` launches in a pane** and the CC badge appears - proves the
   `claude.cmd` -> `perch.exe wrap-claude` PATH shim and the per-pane named
   pipe (`\\.\pipe\perch\<paneId>`) both work packaged. Watching a `perch
   notify` reach the footer is the direct pipe check.
3. **Settings/sessions persist** across a restart (AppData redirect is
   consistent between host and CLI).

To uninstall the test build: `Get-AppxPackage *Perch* | Remove-AppxPackage`.

## Contingency: if the named pipe fails under package identity

It should not - `runFullTrust` runs the app as the plain user token (not an
AppContainer), so the default pipe ACL already covers the same-user CLI. But if
smoke-test step 2 shows the CLI cannot connect (a "host not listening" timeout
while the host is clearly up), harden the pipe ACL to grant the current user
explicitly. In both `PerchIpcServer` and `ControlIpcServer`, replace the
`new NamedPipeServerStream(...)` with:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;

var sec = new PipeSecurity();
sec.AddAccessRule(new PipeAccessRule(
    WindowsIdentity.GetCurrent().User!,
    PipeAccessRights.FullControl, AccessControlType.Allow));
server = NamedPipeServerStreamAcl.Create(
    pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
```

`NamedPipeServerStreamAcl` needs `<PackageReference Include="System.IO.Pipes.AccessControl" />`.
Granting the current user is a no-op regression for the unpackaged build (the
CLI already runs as that same user).

## CI (later)

Wiring `pack-msix.ps1` into `.github/workflows/build.yml` on tag and
auto-submitting via the Store submission API (or `StoreBroker`) is a follow-up.
For the first release, a manual Partner Center upload is fine.
