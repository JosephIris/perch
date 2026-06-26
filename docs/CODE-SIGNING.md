# Code signing

## Why this exists

`Perch-Setup-*.exe` and the portable `Perch.exe` ship **unsigned** by default.
Unsigned, low-prevalence Windows executables — especially a terminal app that
spawns ConPTY processes and runs shell commands — trip two separate warnings:

1. **Microsoft Defender** flags the installer as `Trojan:Win32/Wacatac.B!ml`.
   The `!ml` suffix means it's a machine-learning *heuristic*, not a signature
   match — a false positive. A *compressed* .NET single-file bundle is a known
   trigger (it behaves like a self-extracting packer), which is why
   `EnableCompressionInSingleFile` is set to **false** in `.github/workflows/build.yml`.
2. **SmartScreen** shows "Windows protected your PC / unknown publisher."
   Only an Authenticode signature with reputation removes this.

Code signing fixes both durably. We use **SignPath Foundation**, which signs
open-source releases **for free** — Perch is MIT-licensed (`LICENSE`), so it
qualifies. SignPath signs **server-side**: there is no certificate file or
private key to store in the repo. CI uploads the build, SignPath signs it, CI
publishes the signed result.

## How the CI wiring works

On a tag push (`v*.*.*`), `build.yml`:

1. Packages the installer and the portable zip into a `staging/` folder.
2. Uploads `staging/` as a GitHub Actions artifact (this **zips** it).
3. **Sign step** (`signpath/github-action-submit-signing-request@v2`) submits
   that artifact to SignPath, which signs the nested files per the project's
   *Artifact Configuration* and returns the signed bundle to `signed/`.
4. The release is created from `signed/` if present, else from `staging/`.

The sign step is **gated** on the `SIGNPATH_API_TOKEN` secret:

- **Secret absent** (before onboarding, or on forks) → sign step is skipped, the
  release publishes **unsigned**, and the job logs a `::warning::`. Nothing breaks.
- **Secret present** → the build signs. If signing fails (wrong slug, project not
  approved, etc.) the **job fails loudly** rather than silently shipping unsigned.

## One-time onboarding (you do this once)

Everything above is already wired. To turn signing on, complete SignPath's setup
and populate the secret + variables it references.

1. **Apply to SignPath Foundation:** <https://about.signpath.io/product/open-source>.
   You need a public repo with an OSI-approved license (MIT ✓). Approval is manual
   and can take a few days.
2. **Install the SignPath GitHub app** on the `JosephIris/perch` repo when prompted
   (this is how SignPath verifies the build provenance and fetches the artifact).
3. In the SignPath web console, note/create:
   - **Organization** → its ID.
   - **Project** (e.g. `perch`) → its slug.
   - **Signing policy** (e.g. `release-signing`) → its slug.
   - **Artifact configuration** (e.g. `release-bundle`) → its slug. Configure it to
     match what CI uploads — a `<zip-file>` root (because `upload-artifact` zips the
     folder) containing:
       - `Perch-Setup-<ver>.exe` — an **Inno Setup** installer; enable nested signing
         so its inner `Perch.exe` and `tools/perch.exe` get signed, then the installer
         itself.
       - `Perch-Portable-<ver>.zip` — a `<zip-file>`; sign the nested `Perch.exe` and
         `tools/perch.exe`.
     See the Artifact Configuration reference:
     <https://docs.signpath.io/documentation/artifact-configuration/>.
4. **Create a CI API token** (a CI-user token scoped to the signing policy).
5. **Add the GitHub secret:** repo → Settings → Secrets and variables → Actions →
   *Secrets*:
   - `SIGNPATH_API_TOKEN` = the token from step 4.
6. **Add the GitHub variables** (same page → *Variables*; these are not secret):
   - `SIGNPATH_ORGANIZATION_ID`
   - `SIGNPATH_PROJECT_SLUG`
   - `SIGNPATH_SIGNING_POLICY_SLUG`
   - `SIGNPATH_ARTIFACT_CONFIG_SLUG`
7. Push a tag (`git tag v1.9.1 && git push origin v1.9.1`) and confirm the
   **Sign release bundle (SignPath)** step runs and the release assets are signed.

## Interim mitigation (before signing is live)

- **Report the false positive to Microsoft:**
  <https://www.microsoft.com/wdsi/filesubmission> → "Software developer" → upload
  the exe → mark "Incorrectly detected as malware/PUA." Cleared hashes propagate to
  all Defender installs, usually within a day. Re-submit per release until reputation
  builds.
- The `EnableCompressionInSingleFile=false` change already removes the strongest
  Defender heuristic trigger on its own.

## Notes

- SignPath Foundation issues **OV**-class certificates, so SmartScreen reputation
  still accrues over downloads/time. Signing removes "unknown publisher" and the
  Defender false positive; the very first signed releases may still see a SmartScreen
  prompt until reputation builds. EV-class instant reputation is a paid path only.
- **Do not** re-enable `EnableCompressionInSingleFile` until a signed build is
  confirmed clean by Defender — compression can re-trigger the heuristic even on
  signed binaries.
- Verify a signed download locally: right-click → Properties → **Digital Signatures**,
  or `signtool verify /pa /v Perch-Setup-<ver>.exe`.
