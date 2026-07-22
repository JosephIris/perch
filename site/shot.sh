#!/usr/bin/env bash
# Landing-page screenshot helper for the design loop.
# Usage: bash site/shot.sh <out.png> [width] [height] [url]
# Renders the local preview (headless Chromium) into a PNG. Gives JS a moment
# to run (mascot rig + demo widgets) via --virtual-time-budget.
set -e
OUT="${1:?out path}"
W="${2:-1440}"
H="${3:-2800}"
URL="${4:-http://127.0.0.1:8137/}"
CHROME="/c/Program Files/Google/Chrome/Application/chrome.exe"
"$CHROME" --headless=new --disable-gpu --hide-scrollbars \
  --force-device-scale-factor=1 --window-size="${W},${H}" \
  --virtual-time-budget=2800 --screenshot="$OUT" "$URL" >/dev/null 2>&1
echo "shot -> $OUT (${W}x${H})"
