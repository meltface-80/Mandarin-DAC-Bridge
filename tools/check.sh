#!/usr/bin/env bash
# The bar (dev/quality.md). Green before every push.
#
#   tools/check.sh          every gate, then the whole test suite (what CI runs)
#   tools/check.sh gates    the fast half: file length, the gates' own tests, the build with the
#                           analyzers (every warning an error). The pre-commit hook runs this.
#
# Prints [PASS] or [FAIL] and why. Works on macOS, Linux and Windows (Git Bash).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
what="${1:-all}"
dotnet=dotnet
command -v dotnet >/dev/null 2>&1 || { [ -x "$HOME/.dotnet/dotnet" ] && dotnet="$HOME/.dotnet/dotnet"; }
python=python3
command -v python3 >/dev/null 2>&1 || python=python

fail() { echo "[FAIL] $1"; exit 1; }

$python tools/quality/length.py >/dev/null || { $python tools/quality/length.py | grep FAIL; fail "file length — see dev/quality.md"; }
$python -m unittest discover -s tools/quality -q 2>/dev/null || fail "the gates' own tests — python3 -m unittest discover -s tools/quality"
log=$(mktemp)
trap 'rm -f "$log"' EXIT
"$dotnet" build tests/MandarinDacBridge.Tests -c Release --nologo -v quiet -clp:ErrorsOnly >"$log" 2>&1 \
  || { grep -E "error" "$log" | sort -u | head -20; fail "build or analyzers — fix the finding (dev/quality.md)"; }
if [ "$what" = gates ]; then echo "[PASS] gates"; exit 0; fi

"$dotnet" test tests/MandarinDacBridge.Tests -c Release --nologo --no-build || fail "tests"
echo "[PASS] check"
