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
exit $fail
