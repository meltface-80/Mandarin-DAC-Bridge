#!/bin/bash
#
# Roon Bridge beside Mandarin DAC Bridge, on Linux (optional).
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/linux/roon-bridge.sh)"
#   … -- --uninstall      removes it again (Roon's own uninstaller)
#
# Roon Bridge can't be packaged with the bridge (Roon's licence), so, like
# RoPieee, this fetches Roon's own installer from Roon Labs and runs it. It
# installs Roon Bridge in /opt/RoonBridge as a service of its own, on this
# machine (not in the bridge's Docker container: Roon Bridge runs beside it).
#
# Then, on the bridge's page, switch on "Share when idle" for each DAC Roon
# should use: the bridge lets go of the DAC while nothing plays through it,
# so Roon can take it, and takes it back when Audirvana or Mandarin play.
set -euo pipefail

ACTION=""
for a in "$@"; do [ "$a" = "--uninstall" ] && ACTION="uninstall"; done

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

if [ "$(uname)" != "Linux" ]; then
  echo "This is for Linux. On a Mac, download Roon Bridge from https://download.roonlabs.net/builds/RoonBridge.dmg"
  exit 1
fi
case "$(uname -m)" in
  x86_64)          ARCH="linuxx64" ;;
  aarch64|arm64)   ARCH="linuxarmv8" ;;
  armv7l|armv7*)   ARCH="linuxarmv7hf" ;;
  *) echo "Roon Bridge isn't made for this processor ($(uname -m))."; exit 1 ;;
esac

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
URL="https://download.roonlabs.net/builds/roonbridge-installer-$ARCH.sh"

say "Downloading Roon's installer for Roon Bridge ($ARCH) from Roon Labs…"
curl -fsSL -o "$TMP/roonbridge-installer.sh" "$URL"

SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo"
if [ "$ACTION" = "uninstall" ]; then
  say "Running Roon's uninstaller. It asks before it removes anything."
  $SUDO bash "$TMP/roonbridge-installer.sh" uninstall </dev/tty
  echo "On the bridge's page you can switch \"Share when idle\" off again."
  exit 0
fi
say "Running Roon's installer. It asks before it installs; answer its questions."
$SUDO bash "$TMP/roonbridge-installer.sh" </dev/tty

say "Roon Bridge is installed."
cat <<'EOF'
Next:
  1. Open the bridge's page (http://<this machine>:55500), tap each DAC Roon should use,
     and switch on "Share when idle".
  2. In Roon: Settings → Audio. The DACs appear under this machine's Roon Bridge; enable them.

Roon plays to a DAC only while the bridge isn't playing to it, and the bridge only takes
it back once Roon has stopped and let go. If a DAC says "Shared · RoonBridge is playing",
that's Roon using it.
EOF
