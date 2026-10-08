#!/bin/bash
set -e

echo "🎛  Starting Mandarin DAC Bridge Installer..."

# Determine internal processor microarchitecture
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    RELEASE_FILE="mandarin-bridge-mac-arm64.tar.gz"
    echo "🍏 Apple Silicon architecture detected."
else
    RELEASE_FILE="mandarin-bridge-mac-x64.tar.gz"
    echo "💻 Intel Mac architecture detected."
fi

# Set deployment sandbox folders
TARGET_DIR="$HOME/.mandarin-bridge"
mkdir -p "$TARGET_DIR"
cd "$TARGET_DIR"

# Dynamically resolve and download the latest release tarball payload
echo "⬇ Downloading standalone native runtime assets..."
LATEST_URL=$(curl -s https://github.com | grep "browser_download_url" | grep "$RELEASE_FILE" | cut -d '"' -f 4)

if [ -z "$LATEST_URL" ]; then
    echo "❌ Error: Could not locate the latest release binary. Make sure you have pushed a version tag!"
    exit 1
fi

curl -L -o bridge-package.tar.gz "$LATEST_URL"

# Extract payload data structures
echo "📦 Unpacking installation directory layers..."
tar -xzf bridge-package.tar.gz
rm bridge-package.tar.gz

# Grant executable system permissions to the compiled AOT container
chmod +x MandarinDacBridge

echo "🚀 Launching Mandarin DAC Bridge Engine..."
echo "--------------------------------------------------------"
./MandarinDacBridge
