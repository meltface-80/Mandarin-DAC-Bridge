#!/bin/bash
#
# The Mac installer's tidy-up of an earlier install's ffmpeg-full (tools/mac/install.sh,
# dev/design/install.md, decision 3), run for real against Homebrew. CI's macOS runner runs it
# (checks.yml): it installs and removes Homebrew packages, so it refuses to run anywhere else.
#
#   tests/mac/tidy-on-homebrew.sh <the bridge's own ffmpeg, as CI built it>
#
# Needs Homebrew's servers, like CI's other install steps; the suite's own tests of the tidy-up's choice
# (MacInstallerTests) need nothing.
set -euo pipefail
fail() { echo "::error::$*"; exit 1; }
[ "${CI:-}" = true ] && [ "$(uname)" = Darwin ] || fail "this installs and removes Homebrew packages: CI's macOS runner only"
own="$1"
root="$(cd "$(dirname "$0")/../.." && pwd)"
brew=/opt/homebrew/bin/brew
export HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_INSTALL_CLEANUP=1 HOMEBREW_NO_AUTOREMOVE=1 HOMEBREW_NO_ENV_HINTS=1
installed() { "$brew" list --versions "$1" >/dev/null 2>&1; }

# An earlier install: ffmpeg-full, and one of its packages the owner asked for too.
before="$("$brew" list --formula -1)"
"$brew" install ffmpeg-full >/dev/null
"$brew" install lame >/dev/null
brought="$(comm -13 <(printf '%s\n' "$before" | sort) <("$brew" list --formula -1 | sort) | grep -vx -e ffmpeg-full -e lame || true)"
[ -n "$brought" ] || fail "ffmpeg-full brought no package that wasn't here: nothing to check"
echo "ffmpeg-full brought $(printf '%s\n' "$brought" | wc -l | tr -d ' ') packages"

home="$(mktemp -d)"
app="$home/Mandarin-DAC-Bridge"
mkdir -p "$app"
: > "$app/mandarin-dac-bridge"   # a bridge here
tidy() { HOME="$home" bash "$root/tools/mac/install.sh" --tidy-only </dev/null; }   # no one at the keyboard

# Its own ffmpeg not there: ffmpeg-full may be the one it uses, so nothing goes.
out="$(tidy)"; echo "$out"
printf '%s' "$out" | grep -c "the bridge's own ffmpeg isn't working here" >/dev/null || fail "no refusal while the bridge's ffmpeg doesn't work"
installed ffmpeg-full || fail "ffmpeg-full removed while the bridge's own ffmpeg doesn't work"

# With it: ffmpeg-full and every package only it brought go; one asked for, and all that was here before, stay.
mkdir -p "$app/ffmpeg/bin"
cp "$own" "$app/ffmpeg/bin/ffmpeg"
out="$(tidy)"; echo "$out"
! installed ffmpeg-full || fail "ffmpeg-full is still installed"
for n in $brought; do ! installed "$n" || fail "$n, which only ffmpeg-full brought, is still installed"; done
installed lame || fail "lame, asked for, was removed"
for n in $before; do installed "$n" || fail "$n, installed before, was removed"; done

# Again: nothing to do, nothing said.
out="$(tidy)"
printf '%s' "$out" | grep -c "Removed" >/dev/null && fail "a second tidy-up removed something: $out"
echo "[PASS] the tidy-up removed ffmpeg-full and the packages only it brought, and nothing else"
