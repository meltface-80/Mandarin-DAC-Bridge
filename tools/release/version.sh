#!/usr/bin/env bash
# version.sh — the version a release builds: the .csproj's <Version>, which a release's tag must name
# (vX.Y.Z, dev/release.md). Run by release.yml; tested by WorkflowTests.
#
#   tools/release/version.sh [tag] [csproj]   prints X.Y.Z, or fails with a ::error:: saying why
#
# Before 1.1.19 the version came from the tag alone, and a tag that wasn't vX.Y.Z built "1.0.0".
set -euo pipefail
tag="${1:-}"
csproj="${2:-src/MandarinDacBridge/MandarinDacBridge.csproj}"
v=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$csproj" | head -n 1)
if ! [[ "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "::error::no X.Y.Z <Version> in $csproj (found '$v')" >&2
  exit 1
fi
if [[ -n "$tag" && "$tag" != "v$v" ]]; then
  echo "::error::the release's tag $tag doesn't name the version being released ($v, in $csproj): tag it v$v" >&2
  exit 1
fi
echo "$v"
