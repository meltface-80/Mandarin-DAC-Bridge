#!/bin/bash
#
# The "DAC Bridge" virtual output for macOS (optional): what lets the Music
# app and the Spotify app play through the bridge to a USB DAC, bit-perfect.
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/driver.sh)"
#   … -- --remove        removes it again
#
# Builds the small audio driver (tools/mac/driver, adapted from Arco's) with
# Apple's command line tools, signs it for this Mac, puts it in
# /Library/Audio/Plug-Ins/HAL (it asks for your password) and restarts the
# Mac's audio (silent for a few seconds). Then, on the bridge's page, switch
# on "This Mac: Apple Music & Spotify".
set -euo pipefail

OWNER_REPO="meltface-80/Mandarin-DAC-Bridge"
TARGET="/Library/Audio/Plug-Ins/HAL/DacBridge.driver"
say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

[ "$(uname)" = "Darwin" ] || { echo "This is for macOS."; exit 1; }

if [ "${1:-}" = "--remove" ] || [ "${2:-}" = "--remove" ]; then
  say "Removing the DAC Bridge audio driver (your password)…"
  sudo rm -rf "$TARGET"
  sudo killall coreaudiod 2>/dev/null || true
  echo "Removed."
  exit 0
fi

if ! xcrun --find clang >/dev/null 2>&1; then
  say "This needs Apple's command line tools. A window opens: click Install, wait, then run this again."
  xcode-select --install 2>/dev/null || true
  exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# From this checkout if run from one, else from GitHub.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || true)"
if [ -n "$HERE" ] && [ -f "$HERE/driver/DacBridgeLoopback.c" ]; then
  cp "$HERE/driver/DacBridgeLoopback.c" "$HERE/driver/Info.plist" "$HERE/driver/LICENSE-Arco" "$HERE/driver/NOTICE-Arco" "$TMP/"
else
  for f in DacBridgeLoopback.c Info.plist LICENSE-Arco NOTICE-Arco; do
    curl -fsSL "https://raw.githubusercontent.com/$OWNER_REPO/main/tools/mac/driver/$f" -o "$TMP/$f"
  done
fi

say "Building the DAC Bridge audio driver…"
B="$TMP/DacBridge.driver/Contents"
mkdir -p "$B/MacOS" "$B/Resources"
cp "$TMP/Info.plist" "$B/Info.plist"
cp "$TMP/LICENSE-Arco" "$TMP/NOTICE-Arco" "$B/Resources/"
clang -bundle -O2 -Wall -Wno-multichar -mmacosx-version-min=14.0 -arch arm64 -arch x86_64 \
  -framework CoreAudio -framework CoreFoundation -o "$B/MacOS/DacBridge" "$TMP/DacBridgeLoopback.c"
codesign --force --sign - "$TMP/DacBridge.driver"

say "Installing it (your Mac's password; nothing shows as you type)…"
sudo rm -rf "$TARGET"
sudo cp -R "$TMP/DacBridge.driver" /Library/Audio/Plug-Ins/HAL/
sudo chown -R root:wheel "$TARGET"
sudo killall coreaudiod 2>/dev/null || true
sleep 3

if system_profiler SPAudioDataType 2>/dev/null | grep -q "DAC Bridge"; then
  say "The DAC Bridge output is installed."
else
  say "Installed; macOS may need a moment (or a restart) to show the DAC Bridge output."
fi
echo "On the bridge's page, switch on \"This Mac: Apple Music & Spotify\" and choose the DAC."
echo "To remove it: /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/$OWNER_REPO/main/tools/mac/driver.sh)\" -- --remove"
