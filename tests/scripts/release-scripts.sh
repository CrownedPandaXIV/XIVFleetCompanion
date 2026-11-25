#!/usr/bin/env bash
# Checks the release scripts that work out plugin versions and repo.json (no network, nothing published):
#   bash tests/scripts/release-scripts.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
scripts=.github/scripts
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
fail=0
check() { if [ "$2" = "$3" ]; then echo "PASS $1"; else echo "FAIL $1: got '$2', wanted '$3'"; fail=1; fi; }

cat > "$tmp/repo.json" <<'JSON'
[{"InternalName":"XIVFleetCompanion","AssemblyVersion":"1.0.1.0","DalamudApiLevel":15,
  "DownloadLinkInstall":"https://example.invalid/v1.0.1.zip","DownloadLinkUpdate":"https://example.invalid/v1.0.1.zip",
  "DownloadLinkTesting":"https://example.invalid/v1.0.1.zip","Changelog":"Fixed things.","IsTestingExclusive":false}]
JSON

check "a normal release with no test build is <version>.0" "$(bash $scripts/stable-build-number.sh 1.0.2 "$tmp/repo.json")" "1.0.2.0"
bash $scripts/testing-entry.sh "$tmp/repo.json" 1.0.2.7 https://example.invalid/test.zip 15 > "$tmp/testing.json"
check "a test build is offered as the testing version" "$(jq -r '.[0] | "\(.TestingAssemblyVersion) \(.TestingDalamudApiLevel) \(.DownloadLinkTesting)"' "$tmp/testing.json")" \
  "1.0.2.7 15 https://example.invalid/test.zip"
check "the normal version is left as it was" "$(jq -r '.[0] | "\(.AssemblyVersion) \(.DownloadLinkInstall) \(.DownloadLinkUpdate) \(.Changelog)"' "$tmp/testing.json")" \
  "1.0.1.0 https://example.invalid/v1.0.1.zip https://example.invalid/v1.0.1.zip Fixed things."
check "the normal release after test build 1.0.2.7 is 1.0.2.8, so testers get it too" "$(bash $scripts/stable-build-number.sh 1.0.2 "$tmp/testing.json")" "1.0.2.8"
check "a test build of another version doesn't count" "$(bash $scripts/stable-build-number.sh 1.0.3 "$tmp/testing.json")" "1.0.3.0"
check "no repo.json counts as no test build" "$(bash $scripts/stable-build-number.sh 1.0.2 "$tmp/missing.json")" "1.0.2.0"

# Test builds count from 1 for each version (test-build-number.sh).
check "the first test build of a version is .1" "$(bash $scripts/test-build-number.sh 1.0.2 "$tmp/repo.json")" "1.0.2.1"
check "the next test build is one above the last" "$(bash $scripts/test-build-number.sh 1.0.2 "$tmp/testing.json")" "1.0.2.8"
check "a new version starts at .1 again" "$(bash $scripts/test-build-number.sh 1.0.3 "$tmp/testing.json")" "1.0.3.1"
check "no repo.json counts as no build" "$(bash $scripts/test-build-number.sh 1.0.2 "$tmp/missing.json")" "1.0.2.1"
printf '[{"AssemblyVersion":"1.0.2.8"}]' > "$tmp/released.json"
check "a test build after the normal release of the same version is numbered above it" "$(bash $scripts/test-build-number.sh 1.0.2 "$tmp/released.json")" "1.0.2.9"
printf '[{"AssemblyVersion":"1.0.2.8","TestingAssemblyVersion":"1.0.2.3"}]' > "$tmp/both.json"
check "the newer of the two counts" "$(bash $scripts/test-build-number.sh 1.0.2 "$tmp/both.json")" "1.0.2.9"
check "a version that only starts the same doesn't count (1.0.20 is not 1.0.2)" "$(bash $scripts/test-build-number.sh 1.0.2 <(printf '[{"AssemblyVersion":"1.0.20.5"}]'))" "1.0.2.1"
# The Windows runner's jq ends lines with \r: the test build (first line) must still count.
mkdir -p "$tmp/crlf-bin"
printf '#!/usr/bin/env bash\n%s "$@" | sed "s/$/\\r/"\n' "$(command -v jq)" > "$tmp/crlf-bin/jq"; chmod +x "$tmp/crlf-bin/jq"
check "Windows line endings still count the test build" "$(PATH="$tmp/crlf-bin:$PATH" bash $scripts/test-build-number.sh 1.0.2 "$tmp/both.json")" "1.0.2.9"
printf '[{"AssemblyVersion":"1.0.1.4","TestingAssemblyVersion":"1.0.2.1"}]' > "$tmp/second.json"
check "the second test build of a version is .2, Windows line endings or not" "$(PATH="$tmp/crlf-bin:$PATH" bash $scripts/test-build-number.sh 1.0.2 "$tmp/second.json")" "1.0.2.2"

# The normal release's entry (repo-entry.sh) replaces the whole entry, so the test build stops being offered.
printf '\xEF\xBB\xBF{"InternalName":"XIVFleetCompanion","AssemblyVersion":"1.0.2.8","DalamudApiLevel":15}' > "$tmp/manifest.json"
printf '# Changelog\n\n## 1.0.2 - unreleased\n\n- New.\n' > "$tmp/CHANGELOG.md"
(cd "$tmp" && bash "$OLDPWD/$scripts/repo-entry.sh" manifest.json 1.0.2 https://example.invalid/v1.0.2.zip > entry.json)
check "the normal release replaces the test build" "$(jq -r '.[0] | "\(.AssemblyVersion) \(.TestingAssemblyVersion // "none") \(.DownloadLinkTesting)"' "$tmp/entry.json")" \
  "1.0.2.8 none https://example.invalid/v1.0.2.zip"

if echo '[]' > "$tmp/empty.json" && bash $scripts/testing-entry.sh "$tmp/empty.json" 1.0.2.7 u 15 > /dev/null 2>&1; then
  echo "FAIL an empty repo.json is refused"; fail=1
else
  echo "PASS an empty repo.json is refused"
fi

# ---- Tagging and publishing, with a stand-in gh (tests/scripts/fake-gh.sh) keeping the releases and the Releases
# repo's files in a folder ----
work="$tmp/work"; export FAKE_GH="$tmp/gh"
mkdir -p "$work/.github" "$work/XIVFleetCompanion/bin/x64/Release/XIVFleetCompanion" "$FAKE_GH/files" "$tmp/bin"
cp -r $scripts "$work/.github/scripts"
ln -s "$PWD/tests/scripts/fake-gh.sh" "$tmp/bin/gh"
export PATH="$tmp/bin:$PATH" GH_TOKEN=x RELEASES_TOKEN=y GITHUB_REPOSITORY=CrownedPandaXIV/XIVFleetCompanion
out="$work/XIVFleetCompanion/bin/x64/Release/XIVFleetCompanion"
build() { printf '\xEF\xBB\xBF{"InternalName":"XIVFleetCompanion","AssemblyVersion":"%s","DalamudApiLevel":15}' "$1" > "$out/XIVFleetCompanion.json"; echo "zip $1" > "$out/latest.zip"; }
printf '# Changelog\n\n## 1.0.2 - 2026-11-21\n\n- **New** things.\n\n## 1.0.1 - 2026-11-01\n\n- Old.\n' > "$work/CHANGELOG.md"
cp "$tmp/repo.json" "$FAKE_GH/files/repo.json"
in_work() { (cd "$work" && "$@"); }

in_work bash .github/scripts/tag-release.sh 1.0.2 abc123 > /dev/null
check "tag-release: a new version gets its release, with its changelog section as the notes" "$(cat "$FAKE_GH/releases/v1.0.2/notes")" "- **New** things."
check "tag-release: run again, it changes nothing" "$(in_work bash .github/scripts/tag-release.sh 1.0.2 abc123)" "v1.0.2 is already released; nothing to do."
: > "$FAKE_GH/releases/v1.0.2/notes"
in_work bash .github/scripts/tag-release.sh 1.0.2 abc123 > /dev/null
check "tag-release: an empty description is filled in" "$(cat "$FAKE_GH/releases/v1.0.2/notes")" "- **New** things."
in_work bash .github/scripts/tag-release.sh 1.0.9 abc123 > /dev/null
check "tag-release: a version with no changelog section says only its number" "$(cat "$FAKE_GH/releases/v1.0.9/notes")" "Version 1.0.9"

for n in 3 4 5 6 7 8 9; do build "1.0.3.$n"; in_work bash .github/scripts/publish-testing.sh "1.0.3.$n" > /dev/null; done
check "publish-testing: the test build is offered to testers, the normal version left as it was" \
  "$(jq -r '.[0] | "\(.TestingAssemblyVersion) \(.DownloadLinkTesting) \(.AssemblyVersion)"' "$FAKE_GH/files/repo.json")" \
  "1.0.3.9 https://github.com/CrownedPandaXIV/XIVFleetCompanion/releases/download/testing/XIVFleetCompanion-1.0.3.9.zip 1.0.1.0"
check "publish-testing: only the newest 5 test builds are kept on the testing release" "$(ls "$FAKE_GH/releases/testing/assets" | tr '\n' ' ')" \
  "XIVFleetCompanion-1.0.3.5.zip XIVFleetCompanion-1.0.3.6.zip XIVFleetCompanion-1.0.3.7.zip XIVFleetCompanion-1.0.3.8.zip XIVFleetCompanion-1.0.3.9.zip "
build 1.0.3.1
if in_work bash .github/scripts/publish-testing.sh 1.0.3.2 > /dev/null 2>&1; then echo "FAIL publish-testing: a build of another number is refused"; fail=1
else echo "PASS publish-testing: a build of another number is refused"; fi

build 1.0.2.0
in_work bash .github/scripts/publish-plugin.sh 1.0.2 > /dev/null
check "publish-plugin: the download is attached to the version's release" "$(cat "$FAKE_GH/releases/v1.0.2/assets/XIVFleetCompanion-1.0.2.zip")" "zip 1.0.2.0"
check "publish-plugin: repo.json lists the version, its download and changelog, and no test build" \
  "$(jq -r '.[0] | "\(.AssemblyVersion) \(.DownloadLinkInstall) \(.Changelog) \(.TestingAssemblyVersion // "none")"' "$FAKE_GH/files/repo.json")" \
  "1.0.2.0 https://github.com/CrownedPandaXIV/XIVFleetCompanion/releases/download/v1.0.2/XIVFleetCompanion-1.0.2.zip - New things. none"
rm "$FAKE_GH/files/repo.json"
in_work bash .github/scripts/publish-plugin.sh 1.0.2 > /dev/null
check "publish-plugin: a Releases repo with no repo.json yet gets one" "$(jq -r '.[0].AssemblyVersion' "$FAKE_GH/files/repo.json")" "1.0.2.0"
exit $fail
