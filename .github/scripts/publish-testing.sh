#!/usr/bin/env bash
# Publishes a test build of the plugin for Dalamud's "testing" versions: only players who opted in to the
# plugin's testing builds get it; everyone else stays on the normal version.
#   1. attaches the build's latest.zip to this repo's "testing" pre-release, as XIVFleetCompanion-<build>.zip, then
#   2. adds it to repo.json in XIVFleetCompanion-Releases as TestingAssemblyVersion and DownloadLinkTesting,
#      leaving the normal version's entries as they are.
# A test build is numbered <version>.<run> (1.0.2.7); the next normal release of that version is numbered above
# the last test build (release.yml), so testers get it too.
#
# Usage: publish-testing.sh <build-version>      (needs gh and jq)
#   GH_TOKEN        can upload to this repo's releases
#   RELEASES_TOKEN  can write to XIVFleetCompanion-Releases
set -euo pipefail

build="${1:?build version required}"
out="XIVFleetCompanion/bin/x64/Release/XIVFleetCompanion"
releases="CrownedPandaXIV/XIVFleetCompanion-Releases"
tag="testing"
asset="XIVFleetCompanion-${build}.zip"
url="https://github.com/${GITHUB_REPOSITORY:-CrownedPandaXIV/XIVFleetCompanion}/releases/download/${tag}/${asset}"

[ -f "$out/latest.zip" ] || { echo "No build at $out/latest.zip" >&2; exit 1; }
manifest="$(sed '1s/^\xEF\xBB\xBF//' "$out/XIVFleetCompanion.json")"
built="$(jq -r '.AssemblyVersion' <<<"$manifest")"
[ "$built" = "$build" ] || { echo "The build is $built, not $build" >&2; exit 1; }
api="$(jq -r '.DalamudApiLevel' <<<"$manifest")"

# The testing pre-release in this repo holds the test builds' downloads (the newest few are kept).
if ! gh release view "$tag" >/dev/null 2>&1; then
  gh release create "$tag" --prerelease --latest=false --title "Test builds" \
    --notes "Test builds of the plugin, for players who opted in to its testing versions in Dalamud. Everyone else gets the normal releases."
fi
cp "$out/latest.zip" "$asset"
gh release upload "$tag" "$asset" --clobber
for old in $(gh release view "$tag" --json assets --jq '.assets[].name' | grep '^XIVFleetCompanion-.*\.zip$' | sort -V | head -n -5); do
  gh release delete-asset "$tag" "$old" -y
done
echo "Attached $asset to the $tag release."

# repo.json: the testing fields of the plugin's entry; the normal ones are left as they are.
GH_TOKEN="$RELEASES_TOKEN" gh api "repos/$releases/contents/repo.json" -H 'Accept: application/vnd.github.raw' > current.json
bash "$(dirname "$0")/testing-entry.sh" current.json "$build" "$url" "$api" > repo.json
sha="$(GH_TOKEN="$RELEASES_TOKEN" gh api "repos/$releases/contents/repo.json" --jq .sha)"
GH_TOKEN="$RELEASES_TOKEN" gh api -X PUT "repos/$releases/contents/repo.json" \
  -f message="Plugin test build ${build}" -f content="$(base64 -w0 < repo.json)" -f sha="$sha" --silent
echo "repo.json now offers test build ${build} to players who opted in."
