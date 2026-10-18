#!/bin/bash
#
# Mandarin DAC Bridge for macOS: the one-line installer.
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/install.sh)"
#
# Installs Homebrew (if it isn't there) and Homebrew's ffmpeg-full (ffmpeg
# built with SoX, the only resampler the bridge uses; it sits beside the plain
# ffmpeg Mandarin uses and doesn't replace it), downloads the bridge — one
# native program, built with .NET 10 — into ~/Mandarin-DAC-Bridge, starts it
# now and at every login, and opens its page. Nothing to set up.
#
# The program comes from the repository's latest GitHub Release, checked
# against that release's SHA256SUMS (from 1.1.26; an earlier release is
# installed unchecked): one that differs is refused, and nothing is changed.
# If there is no release, or no build in it for this Mac, it is built here from
# the source instead (a private copy of the .NET SDK is downloaded for that,
# into ~/Mandarin-DAC-Bridge/.dotnet).
#
# Run it again to update; it keeps the settings.
set -euo pipefail

OWNER_REPO="meltface-80/Mandarin-DAC-Bridge"
APP_DIR="$HOME/Mandarin-DAC-Bridge"
BIN="$APP_DIR/mandarin-dac-bridge"
LABEL="app.mandarin.dacbridge"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
PORT=55500

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
xml() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

# The latest release's tag, from where github.com/<repo>/releases/latest leads: "" when there is no
# release; fails when GitHub can't be reached.
latest_tag() {   # https://github.com/<owner>/<repo>
  local to
  to="$(curl -sSIL -o /dev/null -w '%{url_effective}' "$1/releases/latest")" || return 1
  case "$to" in */releases/tag/*) printf '%s' "${to##*/releases/tag/}" ;; esac
}

# A URL into a file: prints 200 when it came, 404 when there is none; fails on anything else.
fetch() {   # url, file
  local code
  code="$(curl -sSL -o "$2" -w '%{http_code}' "$1")" || return 1
  case "$code" in 200|404) printf '%s' "$code" ;; *) return 1 ;; esac
}

# A file's SHA-256 against its line in a SHA256SUMS: 0 it matches, 1 it doesn't (or SHA256SUMS has no
# line for it: release.yml lists every build), 3 its SHA-256 couldn't be worked out.
sum_check() {   # file, its name in the release, SHA256SUMS
  local want got
  want="$(awk -v n="$2" '$2 == n || $2 == "*" n { print tolower($1); exit }' "$3" 2>/dev/null || true)"
  got="$( (shasum -a 256 "$1" 2>/dev/null || openssl dgst -sha256 -r "$1" 2>/dev/null) | awk '{ print tolower($1) }')"
  [ -n "$got" ] || return 3
  [ -n "$want" ] && [ "$got" = "$want" ]
}

if [ "$(uname)" != "Darwin" ]; then echo "This installer is for macOS. On Linux, use Docker (see the README)."; exit 1; fi
case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  *)     RID="osx-x64" ;;
esac

say "Installing Mandarin DAC Bridge. This takes a few minutes; leave this window open."

# 1. Homebrew, the Mac's package installer (it also brings Apple's command line tools).
use_brew() { for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do if [ -x "$b" ]; then eval "$("$b" shellenv)"; return 0; fi; done; return 1; }
if ! command -v brew >/dev/null 2>&1 && ! use_brew; then
  say "First, Homebrew. When it asks for a password, type your Mac's password (nothing shows as you type) and press Return."
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  use_brew || { echo "Homebrew didn't install. Run this line again."; exit 1; }
fi
BREW="$(brew --prefix)"

# 2. ffmpeg, the decoder, built with SoX: a track at a rate the DAC can't take is resampled with SoX or
#    refused (Audio/Soxr.cs), and Homebrew's plain ffmpeg has none. ffmpeg-full has it; Homebrew keeps it
#    off the PATH (keg-only), so the plain one stays Mandarin's, and the bridge finds this one by itself.
say "Checking ffmpeg (with SoX)… The first time, this downloads a few hundred MB."
brew list --versions ffmpeg-full >/dev/null 2>&1 || brew install ffmpeg-full
FFMPEG="$(brew --prefix ffmpeg-full)/bin/ffmpeg"   # named in the launch file: Homebrew can live elsewhere

# 3. The bridge: fetched (and checked) or built first; one already running is stopped only to put the
#    new one in place, so a download refused or failed leaves it as it was.
mkdir -p "$APP_DIR/data"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
unreachable() { echo "Couldn't download from GitHub. Check this Mac's connection and run this line again (nothing was changed)."; exit 1; }

say "Downloading Mandarin DAC Bridge…"
NAME="mandarin-dac-bridge-$RID.tar.gz"
TAG="$(latest_tag "https://github.com/$OWNER_REPO")" || unreachable
NEW=""
if [ -n "$TAG" ]; then
  AT="https://github.com/$OWNER_REPO/releases/download/$TAG"   # the build and its sums from the same release
  got="$(fetch "$AT/$NAME" "$TMP/bridge.tar.gz")" || unreachable
fi
if [ -n "$TAG" ] && [ "$got" = 200 ]; then
  sums="$(fetch "$AT/SHA256SUMS" "$TMP/SHA256SUMS")" || unreachable
  if [ "$sums" = 404 ]; then
    say "This release ($TAG) has no checksums (it came before they did): installing it unchecked."
  elif sum_check "$TMP/bridge.tar.gz" "$NAME" "$TMP/SHA256SUMS"; then
    say "Checked against the release's checksum."
  else
    case $? in
      3) echo "Couldn't work out the download's checksum (shasum). Nothing was changed." ;;
      *) echo "The downloaded build doesn't match the release's checksum, so it isn't installed (nothing was changed). Try again later; if it happens again, please report it." ;;
    esac
    exit 1
  fi
  tar -xzf "$TMP/bridge.tar.gz" -C "$TMP" && [ -f "$TMP/mandarin-dac-bridge" ] \
    || { echo "The release's build couldn't be unpacked. Nothing was changed; run this line again."; exit 1; }
  NEW="$TMP/mandarin-dac-bridge"
else
  say "No ready-made build for this Mac yet: building it here (a few minutes, once)…"
  if ! xcrun --find clang >/dev/null 2>&1; then
    say "The Mac needs Apple's command line tools. A window opens: click Install, wait for it to finish, then paste the install line again."
    xcode-select --install 2>/dev/null || true
    exit 1
  fi
  DOTNET_DIR="$APP_DIR/.dotnet"
  if [ ! -x "$DOTNET_DIR/dotnet" ] || ! "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP/dotnet-install.sh"
    bash "$TMP/dotnet-install.sh" --channel 10.0 --install-dir "$DOTNET_DIR" --no-path
  fi
  curl -fsSL "https://codeload.github.com/$OWNER_REPO/tar.gz/refs/heads/main" | tar -xz -C "$TMP"
  SRC="$(find "$TMP" -maxdepth 1 -type d -name 'Mandarin-DAC-Bridge-*' | head -1)"
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET_DIR/dotnet" publish "$SRC/src/MandarinDacBridge" \
    -c Release -r "$RID" -o "$TMP/out" --nologo -v quiet
  NEW="$TMP/out/mandarin-dac-bridge"
fi
launchctl unload "$PLIST" 2>/dev/null || true   # a second run: the one running is replaced now
mv -f "$NEW" "$BIN"
chmod +x "$BIN"
xattr -d com.apple.quarantine "$BIN" 2>/dev/null || true
codesign --force --sign - "$BIN" >/dev/null 2>&1 || true

say "The USB DACs it can see:"
"$BIN" --list || { echo "The bridge was installed but can't read the Mac's audio devices."; exit 1; }

# 4. Start it now and at every login.
{
  echo '<?xml version="1.0" encoding="UTF-8"?>'
  echo '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">'
  echo '<plist version="1.0"><dict>'
  echo "  <key>Label</key><string>$LABEL</string>"
  echo "  <key>ProgramArguments</key><array><string>$(xml "$BIN")</string></array>"
  echo "  <key>WorkingDirectory</key><string>$(xml "$APP_DIR")</string>"
  echo '  <key>EnvironmentVariables</key><dict>'
  echo "    <key>PORT</key><string>$PORT</string>"
  echo "    <key>DATA_DIR</key><string>$(xml "$APP_DIR/data")</string>"
  echo "    <key>FFMPEG</key><string>$(xml "$FFMPEG")</string>"
  echo "    <key>PATH</key><string>$(xml "$BREW/bin:/usr/bin:/bin")</string>"
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
say "Mandarin DAC Bridge $("$BIN" --version) is running."
echo "  On this Mac:   http://localhost:$PORT"
[ -n "$IP" ] && echo "  On your phone: http://$IP:$PORT"
echo
echo "Each USB DAC is now on your network as \"<its name> (Bridge)\":"
echo "  • Audirvana: Settings → Audio → choose \"<DAC> (Bridge)\" under UPnP, not the DAC itself."
echo "  • Mandarin:  Settings → Audio Devices → choose \"<DAC> (Bridge)\"."
echo "A DAC Audirvana still holds shows \"Waiting\" on the page; the bridge takes it as soon as Audirvana lets go."
echo "If macOS asks to let \"mandarin-dac-bridge\" find devices on your network, choose Allow."
echo "To remove it: /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/$OWNER_REPO/main/tools/mac/uninstall.sh)\""
