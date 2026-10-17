#!/usr/bin/env bash
# Keeps the README of the public XIVFleetCompanion-Releases repo (the download page people see) the same
# as .github/releases-readme.md here. Does nothing when they already match.
#
# Usage: sync-releases-readme.sh      (RELEASES_TOKEN: can write to XIVFleetCompanion-Releases)
set -euo pipefail

releases="CrownedPandaXIV/XIVFleetCompanion-Releases"
api() { GH_TOKEN="$RELEASES_TOKEN" gh api "$@"; }

current="$(api "repos/$releases/contents/README.md" -H 'Accept: application/vnd.github.raw' 2>/dev/null || true)"
if [ "$current" = "$(cat .github/releases-readme.md)" ]; then
  echo "The Releases README is up to date."
  exit 0
fi
sha="$(api "repos/$releases/contents/README.md" --jq .sha 2>/dev/null || true)"
api -X PUT "repos/$releases/contents/README.md" -f message="Update the download page" \
  -f content="$(base64 -w0 < .github/releases-readme.md)" ${sha:+-f sha="$sha"} --silent
echo "Updated the Releases README."
