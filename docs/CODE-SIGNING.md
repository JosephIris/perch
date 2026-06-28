# Code signing

> **⚠ Pipeline moved to Velopack — this doc's CI steps are partly stale.**
> Distribution is now Velopack (`vpk pack` → `Setup.exe` + `.nupkg` feed), not
> the Inno installer + portable zip. The **SignPath *account* onboarding below
> (steps 1–6) is still valid**, but the *CI wiring* and *artifact configuration*
> need re-doing for Velopack: sign the exes **during `vpk pack`** via its
> `--signTemplate` / signtool-compatible hook, rather than as a post-build
> GitHub-artifact signing pass. See **"Re-wiring for Velopack"** below. Until
> that's done, releases publish **unsigned** — same as today (the token was
> never set).

## Why this exists

The Velopack `Setup.exe` and the app's `Perch.exe` ship **unsigned** by default.
Unsigned, low-prevalence Windows executables — especially a terminal app that
spawns ConPTY processes and runs shell commands — trip two separate warnings:

1. **Microsoft Defender** flagged the old installer as `Trojan:Win32/Wacatac.B!ml`.
   The `!ml` suffix means it's a machine-learning *heuristic*, not a signature
   match — a false positive. The strongest trigger was a *compressed* .NET
   single-file bundle (it behaves like a self-extracting packer). **The Velopack
   pipeline publishes a plain self-contained folder, not a single-file bundle**,
   so that heuristic no longer applies — but an unsigned binary can still draw an
   `!ml` flag, so signing remains worthwhile.
2. **SmartScreen** shows "Windows protected your PC / unknown publisher."
   Only an Authenticode signature with reputation removes this.

Code signing fixes both durably. We use **SignPath Foundation**, which signs
open-source releases **for free** — Perch is MIT-licensed (`LICENSE`), so it
qualifies. SignPath signs **server-side**: there is no certificate file or
private key to store in the repo. CI uploads the build, SignPath signs it, CI
publishes the signed result.

## Re-wiring for Velopack (the follow-up)

The previous flow signed a post-build GitHub artifact (the Inno installer +
portable zip). That artifact no longer exists. With Velopack the right place to
sign is **inside `vpk pack`**, which has a built-in hook to invoke a
signtool-compatible command across every exe/dll it bundles *and* the generated
`Setup.exe`:

- `vpk pack ... --signTemplate "<command with {{file}} placeholder>"` — Velopack
  calls it once per file. Point it at whatever produces an Authenticode
  signature (Azure Trusted Signing's `dotnet sign`, a local signtool + cert,
  or SignPath's CLI submitter).
- SignPath publishes a Velopack-specific guide (sign the `./Releases` output, or
  use their CLI as the `--signTemplate` command). Wire `SIGNPATH_API_TOKEN` in as
  the gate exactly as before, but call SignPath from the pack step instead of a
  separate `submit-signing-request` action.

Until this is wired, the `Install/Pack/Publish` steps in `build.yml` run unsigned.
The `SIGNPATH_API_TOKEN` env is still declared there as the intended gate.

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
   - **Artifact configuration** (e.g. `release-bundle`) → its slug. For the
     Velopack pipeline this should match the `./Releases` output of `vpk pack`
     (the `Perch-*-full.nupkg`, the delta `.nupkg`, and `*Setup.exe`), OR — the
     simpler path — drive signing from `vpk pack --signTemplate` so SignPath signs
     each file inline and no separate artifact configuration is needed. See the
     Artifact Configuration reference:
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
7. Push a tag (`git tag v1.9.1 && git push origin v1.9.1`) and confirm the signing
   step runs (the pack step's `--signTemplate`) and the release assets are signed.

## Interim mitigation (before signing is live)

- **Report the false positive to Microsoft:**
  <https://www.microsoft.com/wdsi/filesubmission> → "Software developer" → upload
  the exe → mark "Incorrectly detected as malware/PUA." Cleared hashes propagate to
  all Defender installs, usually within a day. Re-submit per release until reputation
  builds.
- Moving off single-file (the Velopack folder publish) already removes the
  strongest Defender heuristic trigger on its own.

## Notes

- SignPath Foundation issues **OV**-class certificates, so SmartScreen reputation
  still accrues over downloads/time. Signing removes "unknown publisher" and the
  Defender false positive; the very first signed releases may still see a SmartScreen
  prompt until reputation builds. EV-class instant reputation is a paid path only.
- **Do not** reintroduce a compressed single-file publish for the app — it can
  re-trigger the Defender packer heuristic even on signed binaries, and it breaks
  Velopack's file-level delta updates and self-replacement.
- Verify a signed download locally: right-click → Properties → **Digital Signatures**,
  or `signtool verify /pa /v *Setup.exe`.
