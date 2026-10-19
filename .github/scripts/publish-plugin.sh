#!/usr/bin/env bash
# Publishes a built plugin so Dalamud can install and update it:
#   1. attaches the build's latest.zip to this repo's release for the version, then
#   2. writes repo.json (the list Dalamud reads) to the public XIVFleetCompanion-Releases repo.
# Players add https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/repo.json
# under Custom Plugin Repositories in /xlsettings.
#
# Usage: publish-plugin.sh <version>
#   GH_TOKEN        can upload to this repo's releases
#   RELEASES_TOKEN  can write to XIVFleetCompanion-Releases
set -euo pipefail

version="${1:?version required}"
tag="v${version}"
out="XIVFleetCompanion/bin/x64/Release/XIVFleetCompanion"
releases="CrownedPandaXIV/XIVFleetCompanion-Releases"
asset="XIVFleetCompanion-${version}.zip"
url="https://github.com/CrownedPandaXIV/XIVFleetCompanion/releases/download/${tag}/${asset}"

[ -f "$out/latest.zip" ] || { echo "No build at $out/latest.zip" >&2; exit 1; }
bash .github/scripts/repo-entry.sh "$out/XIVFleetCompanion.json" "$version" "$url" > repo.json

# The tag-release job creates the release; wait briefly in case GitHub is still catching up.
for _ in 1 2 3 4 5 6; do gh release view "$tag" >/dev/null 2>&1 && break; sleep 10; done
cp "$out/latest.zip" "$asset"
gh release upload "$tag" "$asset" --clobber
echo "Attached $asset to $tag."

# Write repo.json to the Releases repo (its README is kept up to date by sync-releases-readme.sh).
put() {
  local path="$1" file="$2" message="$3" sha
  sha="$(GH_TOKEN="$RELEASES_TOKEN" gh api "repos/$releases/contents/$path" --jq .sha 2>/dev/null || true)"
  GH_TOKEN="$RELEASES_TOKEN" gh api -X PUT "repos/$releases/contents/$path" \
    -f message="$message" -f content="$(base64 -w0 < "$file")" ${sha:+-f sha="$sha"} --silent
}
put repo.json repo.json "Plugin ${version}"
echo "repo.json now lists ${version}."

