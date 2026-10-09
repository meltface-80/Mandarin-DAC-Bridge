#!/bin/bash
#
# Removes Mandarin DAC Bridge from a Mac, and gives the DACs back to macOS.
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/uninstall.sh)"
#
# Homebrew and ffmpeg stay (Mandarin uses them too).
set -uo pipefail

LABEL="app.mandarin.dacbridge"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

launchctl unload "$PLIST" 2>/dev/null
rm -f "$PLIST"
rm -rf "$HOME/Mandarin-DAC-Bridge"
echo "Mandarin DAC Bridge is removed. The DACs are the Mac's again."
