#!/bin/bash
#
# Removes Mandarin DAC Bridge from a Mac, and gives the DACs back to macOS.
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/uninstall.sh)"
#
# Its own ffmpeg goes with its folder. Homebrew and its ffmpegs stay (Mandarin uses them too); the
# ffmpeg-full that earlier installers added, if it was kept, is removed by the install line's tidy-up
# alone (-- --tidy-only), with the packages only it needed.
set -uo pipefail

LABEL="app.mandarin.dacbridge"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

launchctl unload "$PLIST" 2>/dev/null
rm -f "$PLIST"
rm -rf "$HOME/Mandarin-DAC-Bridge"
echo "Mandarin DAC Bridge is removed. The DACs are the Mac's again."
for b in /opt/homebrew/bin/brew /usr/local/bin/brew; do
  if [ -x "$b" ] && "$b" list --versions ffmpeg-full >/dev/null 2>&1; then
    echo "Homebrew's ffmpeg-full (added by earlier installers) was left as it is. To remove it and the packages only it needed:"
    echo "  /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/mac/install.sh)\" -- --tidy-only"
    break
  fi
done
