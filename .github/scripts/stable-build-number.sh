#!/usr/bin/env bash
# Prints the four-part version to build a normal release as: <version>.0, or one above the last test build of the
# same version that repo.json offers (1.0.2.8 after test build 1.0.2.7), so players on that test build get the
# normal release too.
#
# Usage: stable-build-number.sh <version> <repo.json>      (needs jq; a missing repo.json counts as no test build)
set -euo pipefail
version="${1:?version required}"
testing="$(jq -r '.[0].TestingAssemblyVersion // empty' "${2:?repo.json required}" 2>/dev/null || true)"
case "$testing" in
  "$version".*) echo "${version}.$(( ${testing##*.} + 1 ))" ;;
  *) echo "${version}.0" ;;
esac
