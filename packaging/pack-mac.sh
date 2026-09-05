#!/bin/bash
# Build + package the mac app: Release publish (osx-arm64, self-contained)
# → vpk pack → Perch.app + installer zip + Velopack update artifacts in
# packaging/mac-releases/. Run from the repo root:
#
#   bash packaging/pack-mac.sh [version]
#
# Prereqs: dotnet 8 SDK, node (web bundle), `dotnet tool install -g vpk`.
# Unsigned (ad-hoc) — fine for local installs; a Developer ID + notarization
# step slots in via vpk's --signAppIdentity when we have one.
set -euo pipefail

VERSION="${1:-0.1.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUB="$ROOT/packaging/mac-publish"
OUT="$ROOT/packaging/mac-releases"
ICONSET="$ROOT/packaging/mac-icon.iconset"
ICNS="$ROOT/packaging/perch.icns"

# ---- icon: the rounded-squircle logo art + standard Dock margins --------
if [ ! -f "$ICNS" ]; then
  rm -rf "$ICONSET"; mkdir -p "$ICONSET"
  swift "$ROOT/packaging/gen-mac-icon.swift" "$ROOT/src/web/perch-logo.png" "$ICONSET"
  iconutil -c icns "$ICONSET" -o "$ICNS"
  rm -rf "$ICONSET"
fi

# ---- web bundle FIRST -----------------------------------------------------
# The csproj's <Content Include="..\Perch\wwwroot\**"> is evaluated when
# MSBuild loads the project, before any target runs. On a checkout that has
# never built (or after a clean) wwwroot/ does not exist yet, the Content set
# is empty, and `dotnet publish` ships an app with no page — it opens on
# "Web bundle not found". Bundling here makes the folder exist at evaluation
# time. Same rule as the CI workflow's "Build web bundle" step.
if [ ! -d "$ROOT/src/web/node_modules" ]; then
  npm --prefix "$ROOT/src/web" ci --no-audit --no-fund
fi
npm --prefix "$ROOT/src/web" run build

# ---- publish (perch-cli publish rides along via the csproj) ---------------
rm -rf "$PUB"
dotnet publish "$ROOT/src/Perch.Mac/Perch.Mac.csproj" \
  -c Release -r osx-arm64 --self-contained true \
  -o "$PUB" --nologo

# Refuse to pack an app that would open on "Web bundle not found".
if [ ! -f "$PUB/wwwroot/app.js" ]; then
  echo "pack-mac: $PUB/wwwroot/app.js missing — the web bundle did not ship" >&2
  exit 1
fi

# The tools dir (perch CLI + shims) is staged by an AfterTargets=Build hook
# into the build OutDir; make sure publish carries it too.
if [ ! -d "$PUB/tools" ]; then
  cp -R "$ROOT/src/Perch.Mac/bin/Release/net8.0/osx-arm64/tools" "$PUB/tools"
fi

# ---- pack ----------------------------------------------------------------
vpk pack \
  --packId Perch \
  --packVersion "$VERSION" \
  --packDir "$PUB" \
  --mainExe Perch \
  --packTitle perch \
  --packAuthors "Joseph Iris" \
  --icon "$ICNS" \
  --outputDir "$OUT"

echo
echo "artifacts in $OUT:"
ls -la "$OUT"
