#!/bin/bash
#
# The now-playing screen on a monitor plugged into this Linux machine (optional).
#
#   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/linux/display.sh)"
#   … -- --url http://192.168.1.20:55500/now    a bridge on another machine
#   … -- --uninstall                            back to the console
#
# Installs cage (a one-window Wayland "kiosk") and Chromium, and a service
# that shows the bridge's /now page full-screen from boot: cover, title,
# artist, album, time and format of whatever is playing; a clock when
# nothing is. No desktop needed (DietPi, Raspberry Pi OS Lite, Debian,
# Ubuntu Server). The bridge can run here (natively or in Docker) or on
# another machine on the network.
#
# To show one DAC only, add ?dac=<id> to the URL (the picker on the screen,
# shown when a mouse moves, does the same).
set -euo pipefail

URL="http://localhost:55500/now"
UNINSTALL=0
while [ $# -gt 0 ]; do
  case "$1" in
    --url) URL="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    --) shift ;;
    *) echo "unknown option: $1"; exit 1 ;;
  esac
done

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
[ "$(uname)" = "Linux" ] || { echo "This is for Linux. On a Mac, open $URL in a browser and make it full-screen."; exit 1; }
SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo"

SERVICE=/etc/systemd/system/dac-bridge-screen.service
SCREEN_USER=dacscreen

if [ "$UNINSTALL" = 1 ]; then
  say "Removing the now-playing screen…"
  $SUDO systemctl disable --now dac-bridge-screen.service 2>/dev/null || true
  $SUDO rm -f "$SERVICE" /etc/pam.d/dac-bridge-screen
  $SUDO systemctl daemon-reload
  $SUDO userdel -r "$SCREEN_USER" 2>/dev/null || true
  echo "Done. (cage and Chromium are left installed: sudo apt-get remove cage chromium)"
  exit 0
fi

command -v apt-get >/dev/null || { echo "This needs a Debian-family system (apt-get): DietPi, Raspberry Pi OS, Debian, Ubuntu."; exit 1; }

say "Installing cage and Chromium (a few minutes)…"
$SUDO apt-get update
$SUDO apt-get install -y --no-install-recommends cage fonts-dejavu-core
$SUDO apt-get install -y --no-install-recommends chromium 2>/dev/null || $SUDO apt-get install -y --no-install-recommends chromium-browser
CHROMIUM="$(command -v chromium || command -v chromium-browser)"
CAGE="$(command -v cage)"

say "Setting up the screen…"
# Its own user, allowed to use the display and the keyboard/mouse.
if ! id "$SCREEN_USER" >/dev/null 2>&1; then
  $SUDO useradd --create-home --shell /usr/sbin/nologin "$SCREEN_USER"
fi
for g in video render input tty; do getent group "$g" >/dev/null && $SUDO usermod -aG "$g" "$SCREEN_USER"; done

# A login session on tty7 for it (what cage needs to take the screen).
$SUDO tee /etc/pam.d/dac-bridge-screen >/dev/null <<'EOF'
auth     required pam_unix.so nullok
account  required pam_unix.so
session  required pam_unix.so
session  required pam_systemd.so
EOF

$SUDO tee "$SERVICE" >/dev/null <<EOF
[Unit]
Description=Mandarin DAC Bridge now-playing screen
After=systemd-user-sessions.service network-online.target
Wants=network-online.target
Conflicts=getty@tty7.service

[Service]
Type=simple
User=$SCREEN_USER
PAMName=dac-bridge-screen
TTYPath=/dev/tty7
TTYReset=yes
TTYVHangup=yes
TTYVTDisallocate=yes
StandardInput=tty-fail
UtmpIdentifier=tty7
UtmpMode=user
ExecStartPre=/bin/chvt 7
ExecStart=$CAGE -- $CHROMIUM --kiosk --ozone-platform=wayland --noerrdialogs --disable-infobars --no-first-run \\
  --disable-session-crashed-bubble --disable-features=Translate --check-for-update-interval=31536000 \\
  --password-store=basic --incognito $URL
Restart=always
RestartSec=5

[Install]
WantedBy=graphical.target
EOF

$SUDO systemctl daemon-reload
$SUDO systemctl set-default graphical.target >/dev/null
$SUDO systemctl enable --now dac-bridge-screen.service

say "The now-playing screen is on."
echo "It shows $URL, and starts by itself at boot."
echo "To stop it: sudo systemctl disable --now dac-bridge-screen   To remove it: run this again with --uninstall"
