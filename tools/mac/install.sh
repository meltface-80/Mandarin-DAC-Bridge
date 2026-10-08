#!/bin/bash
#
# Mandarin DAC Bridge for macOS: the one-line installer.
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/install.sh)"
#
# Installs Homebrew (if it isn't there), Node.js 22 and ffmpeg (the same
# ones Mandarin uses: nothing new if Mandarin is installed), downloads the
# bridge into ~/Mandarin-DAC-Bridge, builds its small Core Audio helper,
# starts it now and at every login, and opens its page. Nothing to set up.
# Run it again to update; it keeps the settings.
set -euo pipefail

APP_DIR="$HOME/Mandarin-DAC-Bridge"
LABEL="app.mandarin.dacbridge"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
REPO="https://github.com/meltface-80/Mandarin-DAC-Bridge.git"
PORT=55500

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
xml() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

if [ "$(uname)" != "Darwin" ]; then echo "This installer is for macOS. On Linux, use Docker (see the README)."; exit 1; fi

say "Installing Mandarin DAC Bridge. This takes a few minutes; leave this window open."

# 1. Homebrew, the Mac's package installer (it also brings Apple's command line tools).
use_brew() { for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do if [ -x "$b" ]; then eval "$("$b" shellenv)"; return 0; fi; done; return 1; }
if ! command -v brew >/dev/null 2>&1 && ! use_brew; then
  say "First, Homebrew. When it asks for a password, type your Mac's password (nothing shows as you type) and press Return."
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  use_brew || { echo "Homebrew didn't install. Run this line again."; exit 1; }
fi
BREW="$(brew --prefix)"

# 2. Node.js 22 and ffmpeg (skipped when they're there already, e.g. for Mandarin).
say "Checking Node.js and ffmpeg…"
brew list --versions node@22 >/dev/null 2>&1 || brew install node@22
brew list --versions ffmpeg >/dev/null 2>&1 || brew install ffmpeg
NODE_BIN="$BREW/opt/node@22/bin"
export PATH="$NODE_BIN:$BREW/bin:$PATH"

# 3. Apple's compiler, for the helper (Homebrew installs it; this is in case it was removed).
if ! xcrun --find clang >/dev/null 2>&1; then
  say "The Mac needs Apple's command line tools. A window opens: click Install, wait for it to finish, then paste the install line again."
  xcode-select --install 2>/dev/null || true
  exit 1
fi

# 4. The bridge itself (stopped first if this is a second run).
launchctl unload "$PLIST" 2>/dev/null || true
if [ -d "$APP_DIR/.git" ]; then
  say "Updating Mandarin DAC Bridge…"
  git -C "$APP_DIR" fetch --depth 1 origin main
  git -C "$APP_DIR" reset --hard origin/main
else
  say "Downloading Mandarin DAC Bridge…"
  rm -rf "$APP_DIR"
  git clone --depth 1 "$REPO" "$APP_DIR"
fi
mkdir -p "$APP_DIR/data" "$APP_DIR/bin"

# 5. The Core Audio helper: holds each DAC in exclusive (hog) mode and sets its rate.
say "Building the Core Audio helper…"
xcrun clang -O2 -Wall -o "$APP_DIR/bin/dachelper" "$APP_DIR/helper/dachelper-mac.c" \
  -framework CoreAudio -framework CoreFoundation -lpthread
"$APP_DIR/bin/dachelper" list >/dev/null || { echo "The helper was built but can't read the Mac's audio devices."; exit 1; }

# 6. Start it now and at every login.
{
  echo '<?xml version="1.0" encoding="UTF-8"?>'
  echo '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">'
  echo '<plist version="1.0"><dict>'
  echo "  <key>Label</key><string>$LABEL</string>"
  echo "  <key>ProgramArguments</key><array><string>$(xml "$NODE_BIN/node")</string><string>$(xml "$APP_DIR/bridge.js")</string></array>"
  echo "  <key>WorkingDirectory</key><string>$(xml "$APP_DIR")</string>"
  echo '  <key>EnvironmentVariables</key><dict>'
  echo "    <key>PORT</key><string>$PORT</string>"
  echo "    <key>DATA_DIR</key><string>$(xml "$APP_DIR/data")</string>"
  echo "    <key>PATH</key><string>$(xml "$NODE_BIN:$BREW/bin:/usr/bin:/bin")</string>"
  echo '  </dict>'
  echo '  <key>RunAtLoad</key><true/>'
  echo '  <key>KeepAlive</key><true/>'
  echo '  <key>ProcessType</key><string>Interactive</string>'
  echo "  <key>StandardOutPath</key><string>$(xml "$APP_DIR/data/bridge.log")</string>"
  echo "  <key>StandardErrorPath</key><string>$(xml "$APP_DIR/data/bridge.log")</string>"
  echo '</dict></plist>'
} > "$PLIST"
launchctl load "$PLIST"

say "Starting Mandarin DAC Bridge…"
up=""
for _ in $(seq 1 30); do
  if curl -fs "http://localhost:$PORT/api/health" >/dev/null 2>&1; then up=1; break; fi
  sleep 1
done
if [ -z "$up" ]; then
  echo "It didn't start. What it said is in $APP_DIR/data/bridge.log"
  exit 1
fi
open "http://localhost:$PORT"

IP="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)"
say "Mandarin DAC Bridge is running."
echo "  On this Mac:   http://localhost:$PORT"
[ -n "$IP" ] && echo "  On your phone: http://$IP:$PORT"
echo
echo "Each USB DAC is now on your network as \"<its name> (Bridge)\":"
echo "  • Audirvana: Settings → Audio → choose \"<DAC> (Bridge)\" under UPnP, not the DAC itself."
echo "  • Mandarin:  Settings → Audio Devices → choose \"<DAC> (Bridge)\"."
echo "A DAC Audirvana still holds shows \"Waiting\" on the page; the bridge takes it as soon as Audirvana lets go."
echo "If macOS asks to let \"node\" find devices on your network, choose Allow."
echo "To remove it: /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/uninstall.sh)\""
