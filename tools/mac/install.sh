#!/bin/bash
#
# Mandarin DAC Bridge for macOS: the one-line installer.
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/install.sh)"
#
# Downloads the bridge (one native program, built with .NET 10) and its own small ffmpeg with SoX, the
# only resampler the bridge uses, into ~/Mandarin-DAC-Bridge, starts it now and at every login, and
# opens its page. Nothing to set up, and nothing else installed: no Homebrew
# (dev/design/install.md).
#
# Both come from the repository's latest GitHub Release, checked against that release's SHA256SUMS
# (from 1.1.26; an earlier release is installed unchecked): one that differs is refused, and nothing is
# changed. If there is no release, or no build in it for this Mac, the bridge is built here from the
# source instead (a private copy of the .NET SDK is downloaded for that, into
# ~/Mandarin-DAC-Bridge/.dotnet), and its ffmpeg too (tools/ffmpeg/build.sh, from ffmpeg's and
# libsoxr's pinned source), with Apple's command line tools.
#
# Homebrew's ffmpeg-full, which earlier installers added, is removed with the packages only it
# needed, once the bridge's own ffmpeg works and the person at the Mac says yes (Homebrew and
# Mandarin's ffmpeg stay): "an earlier install's ffmpeg-full", below. Run it again to update; it keeps
# the settings. Only the tidy-up: /bin/bash -c "$(curl -fsSL …/install.sh)" -- --tidy-only
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

# ---------------------------------------------------------------- an earlier install's ffmpeg-full
#
# Installers before the bridge had its own ffmpeg added Homebrew's ffmpeg-full, and with it about a
# hundred packages. Once the bridge's own ffmpeg works here, they are removed (dev/design/install.md,
# decision 3 as changed on 2026-10-19): ffmpeg-full, if nothing installed needs it, and each package it
# brought that nothing else installed needs and nobody asked for (Homebrew's "installed on request"),
# then Homebrew's downloads of them. Homebrew itself and Mandarin's plain ffmpeg stay. The person at the
# Mac is asked first (Return: yes); with no one at the keyboard it is done without asking.

# ffmpeg-full and what only it needs, one per line; "NEEDED <by>" when something else needs ffmpeg-full.
# From one `brew info` of everything installed, worked out with JavaScript for Automation, which every
# Mac has (python3 may not be there, and asking for it can open Apple's tools installer).
ffmpeg_full_tree() {   # brew, a scratch folder
  "$1" info --json=v2 --installed > "$2/installed.json" 2>/dev/null || return 1
  cat > "$2/tree.js" <<'JS'
ObjC.import('Foundation');
function run(argv) {
  var j = JSON.parse(ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(argv[0], $.NSUTF8StringEncoding, null)));
  var root = argv[1], f = {}, users = {};
  (j.formulae || []).forEach(function (x) {
    var i = (x.installed || [])[0] || {};
    f[x.name] = { req: !!i.installed_on_request, deps: (i.runtime_dependencies || []).map(function (d) { return d.full_name; }) };
  });
  Object.keys(f).forEach(function (n) { f[n].deps.forEach(function (d) { (users[d] = users[d] || []).push(n); }); });
  (j.casks || []).forEach(function (c) {
    ((c.depends_on || {}).formula || []).forEach(function (d) { (users[d] = users[d] || []).push('the app ' + c.token); });
  });
  if (!f[root]) return '';
  if ((users[root] || []).length) return 'NEEDED ' + users[root].join(', ');
  var tree = {}, todo = f[root].deps.slice();
  while (todo.length) { var d = todo.pop(); if (tree[d] || !f[d]) continue; tree[d] = true; todo = todo.concat(f[d].deps); }
  var gone = {}; gone[root] = true;
  for (var changed = true; changed;) {
    changed = false;
    Object.keys(tree).forEach(function (d) {
      if (gone[d] || f[d].req) return;
      if ((users[d] || []).every(function (u) { return gone[u]; })) { gone[d] = true; changed = true; }
    });
  }
  return Object.keys(gone).join('\n');
}
JS
  osascript -l JavaScript "$2/tree.js" "$2/installed.json" ffmpeg-full
}

# Whether an ffmpeg has SoX. grep -c reads all it is given: with pipefail, grep -q could stop reading
# first, and ffmpeg, killed by SIGPIPE, would make a yes look like a no.
has_sox() { "$1" -hide_banner -buildconf 2>&1 | grep -c -- '--enable-libsoxr' >/dev/null; }

tidy_old_ffmpeg() {
  local brew="" b t list n s kb=0 count=0 answer orphans remove="" gone=0 stayed="" kept="" tty="" cellar
  for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do if [ -x "$b" ]; then brew="$b"; break; fi; done
  [ -n "$brew" ] && "$brew" list --versions ffmpeg-full >/dev/null 2>&1 || return 0
  # A bridge here must have its own ffmpeg working first; with no bridge here (uninstalled), nothing of
  # the bridge's needs ffmpeg-full.
  if [ -e "$BIN" ] && ! has_sox "$FFMPEG"; then
    echo "Homebrew's ffmpeg-full stays: the bridge's own ffmpeg isn't working here, so ffmpeg-full may be the one it uses."
    return 0
  fi
  # NO_AUTOREMOVE: Homebrew's uninstall would otherwise remove every unneeded package on the Mac, not only
  # ffmpeg-full's. What goes is the list worked out below, and nothing else.
  export HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_INSTALL_CLEANUP=1 HOMEBREW_NO_ENV_HINTS=1 HOMEBREW_NO_AUTOREMOVE=1
  t="$(mktemp -d)"
  list="$(ffmpeg_full_tree "$brew" "$t")" || list="?"
  rm -rf "$t"
  case "$list" in
    "?") echo "Couldn't read what Homebrew has installed: ffmpeg-full was left as it is."; return 0 ;;
    NEEDED*) echo "Homebrew's ffmpeg-full stays: ${list#NEEDED } needs it."; return 0 ;;
    "") return 0 ;;
  esac
  cellar="$("$brew" --cellar 2>/dev/null)" || cellar="/nonexistent"
  while IFS= read -r n; do
    [ -n "$n" ] || continue
    count=$((count + 1))
    s="$(du -sk "$cellar/$n" 2>/dev/null | awk '{ print $1 }')" || s=0
    kb=$((kb + ${s:-0}))
  done <<< "$list"
  say "Tidying up: earlier versions of this installer added Homebrew's ffmpeg-full, which the bridge no longer needs."
  echo "Removing it frees about $((kb / 1024)) MB: ffmpeg-full and the $((count - 1)) packages only it needed. Nothing you"
  echo "installed yourself goes, nor anything else uses, nor Homebrew, nor Mandarin's ffmpeg:"
  printf '%s\n' "$list" | tr '\n' ' ' | fold -s -w 100; echo
  # The person at the Mac is asked through the terminal: stdin when it is one, else /dev/tty (curl … | bash);
  # with neither, no one is there to ask.
  if [ -t 0 ]; then tty=/dev/stdin; elif (: </dev/tty) 2>/dev/null; then tty=/dev/tty; fi
  if [ -n "$tty" ]; then
    printf 'Remove them? [Y/n] '
    read -r answer <"$tty" || answer=""
    case "$answer" in
      [nN]*) echo "Left as they are. To tidy up later: /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/$OWNER_REPO/main/tools/mac/install.sh)\" -- --tidy-only"; return 0 ;;
    esac
  fi
  "$brew" uninstall ffmpeg-full >/dev/null || { echo "Homebrew couldn't remove ffmpeg-full: nothing else was removed."; return 0; }
  # Removed only where Homebrew itself now counts it unneeded, too.
  orphans=" $("$brew" autoremove --dry-run 2>/dev/null | grep -v '^==>' | tr '\n' ' ') " || orphans=" "
  while IFS= read -r n; do
    [ -n "$n" ] && [ "$n" != ffmpeg-full ] || continue
    case "$orphans" in *" $n "*) remove="$remove $n" ;; *) kept="$kept $n" ;; esac
  done <<< "$list"
  # shellcheck disable=SC2086 # one name each
  [ -z "$remove" ] || "$brew" uninstall $remove >/dev/null 2>&1 || true
  # shellcheck disable=SC2086
  "$brew" cleanup ffmpeg-full $remove >/dev/null 2>&1 || true
  for n in $remove; do
    if "$brew" list --versions "$n" >/dev/null 2>&1; then stayed="$stayed $n"; else gone=$((gone + 1)); fi
  done
  echo "Removed ffmpeg-full and $gone packages only it needed."
  [ -z "$stayed" ] || echo "Homebrew couldn't remove:$stayed"
  [ -z "$kept" ] || echo "Kept, as Homebrew still counts them needed:$kept"
}

if [ "$(uname)" != "Darwin" ]; then echo "This installer is for macOS. On Linux, use Docker (see the README)."; exit 1; fi
case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  *)     RID="osx-x64" ;;
esac

FFMPEG="$APP_DIR/ffmpeg/bin/ffmpeg"   # the bridge's own: looked for there first (Config.FfmpegFolders)

# Only the tidy-up (run with: -- --tidy-only): for one who said no, and for CI.
if [ "${1:-}" = "--tidy-only" ]; then tidy_old_ffmpeg; exit 0; fi

say "Installing Mandarin DAC Bridge. This takes a minute or two; leave this window open."

# 1. The bridge and its ffmpeg (the decoder, built with SoX: a track at a rate the DAC can't take is
#    resampled with SoX or refused, Audio/Soxr.cs): fetched (and checked) or built first. One already
#    running is stopped only to put the new ones in place, so a download refused or failed leaves it as it was.
mkdir -p "$APP_DIR/data"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
unreachable() { echo "Couldn't download from GitHub. Check this Mac's connection and run this line again (nothing was changed)."; exit 1; }

# Building here needs Apple's command line tools (clang, make): asked for once, then the line is run again.
need_tools() {
  if ! xcrun --find clang >/dev/null 2>&1; then
    say "The Mac needs Apple's command line tools. A window opens: click Install, wait for it to finish, then paste the install line again."
    xcode-select --install 2>/dev/null || true
    exit 1
  fi
}
SRC=""
source_tree() {   # the repository's main, fetched once
  if [ -z "$SRC" ]; then
    curl -fsSL "https://codeload.github.com/$OWNER_REPO/tar.gz/refs/heads/main" | tar -xz -C "$TMP" || unreachable
    SRC="$(find "$TMP" -maxdepth 1 -type d -name 'Mandarin-DAC-Bridge-*' | head -1)"
  fi
}

say "Downloading Mandarin DAC Bridge…"
NAME="mandarin-dac-bridge-$RID.tar.gz"
TAG="$(latest_tag "https://github.com/$OWNER_REPO")" || unreachable
NEW=""
NEWFF=""
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
  [ -x "$TMP/ffmpeg/bin/ffmpeg" ] && NEWFF="$TMP/ffmpeg"   # a release that carries its own ffmpeg
else
  say "No ready-made build for this Mac yet: building it here (a few minutes, once)…"
  need_tools
  DOTNET_DIR="$APP_DIR/.dotnet"
  if [ ! -x "$DOTNET_DIR/dotnet" ] || ! "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP/dotnet-install.sh"
    bash "$TMP/dotnet-install.sh" --channel 10.0 --install-dir "$DOTNET_DIR" --no-path
  fi
  source_tree
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET_DIR/dotnet" publish "$SRC/src/MandarinDacBridge" \
    -c Release -r "$RID" -o "$TMP/out" --nologo -v quiet
  NEW="$TMP/out/mandarin-dac-bridge"
fi
if [ -z "$NEWFF" ]; then
  # Built here, or a release from before its ffmpeg came with it: ffmpeg is built from its and libsoxr's
  # pinned source (a minute or two), and nothing but the program is installed.
  say "Building its ffmpeg (with SoX) here, once…"
  need_tools
  source_tree
  bash "$SRC/tools/ffmpeg/build.sh" "$RID" "$TMP/ffmpeg-built" \
    || { echo "Its ffmpeg couldn't be built (what went wrong is above). Nothing was changed."; exit 1; }
  NEWFF="$TMP/ffmpeg-built"
fi
launchctl unload "$PLIST" 2>/dev/null || true   # a second run: the one running is replaced now
mv -f "$NEW" "$BIN"
chmod +x "$BIN"
rm -rf "$APP_DIR/ffmpeg"
mv "$NEWFF" "$APP_DIR/ffmpeg"
for f in "$BIN" "$FFMPEG"; do
  xattr -d com.apple.quarantine "$f" 2>/dev/null || true
  codesign --force --sign - "$f" >/dev/null 2>&1 || true
done
# Not named in the launch file: if it doesn't run on this Mac, the bridge takes another ffmpeg with SoX.
has_sox "$FFMPEG" \
  || echo "Its own ffmpeg was installed but didn't answer here as it should: the bridge will use another ffmpeg with SoX, if this Mac has one, and its log says which."

say "The USB DACs it can see:"
"$BIN" --list || { echo "The bridge was installed but can't read the Mac's audio devices."; exit 1; }

# 2. Start it now and at every login. Homebrew's folder stays on its PATH where there is one (Mandarin's).
LAUNCH_PATH="/usr/bin:/bin"
for b in /opt/homebrew/bin /usr/local/bin; do if [ -x "$b/brew" ]; then LAUNCH_PATH="$b:$LAUNCH_PATH"; break; fi; done
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
  echo "    <key>PATH</key><string>$(xml "$LAUNCH_PATH")</string>"
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
tidy_old_ffmpeg
echo "To remove it: /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/$OWNER_REPO/main/tools/mac/uninstall.sh)\""
