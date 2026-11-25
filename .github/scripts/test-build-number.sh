#!/usr/bin/env bash
# Prints the four-part version to build a test build as: <version>.1 for the first test build of a version, then one
# above the newest build of that version repo.json offers (its test build, or a normal release of it): 1.0.2.4 after
# test build 1.0.2.3, 1.0.2.9 after normal release 1.0.2.8, so players on either get it. A new version starts at .1
# again; Dalamud compares the numbers left to right, so 1.0.3.1 is newer than 1.0.2.9.
#
# Usage: test-build-number.sh <version> <repo.json>      (needs jq; a missing repo.json counts as no build)
set -euo pipefail
version="${1:?version required}"
last=0
# tr: jq on the Windows runner ends its lines with \r, which would hide the test build (the first of the two lines).
for listed in $({ jq -r '.[0] | .TestingAssemblyVersion // empty, .AssemblyVersion // empty' "${2:?repo.json required}" 2>/dev/null || true; } | tr -d '\r'); do
  case "$listed" in
    "$version".*) n="${listed##*.}"; [[ "$n" =~ ^[0-9]+$ ]] && [ "$n" -gt "$last" ] && last="$n" ;;
  esac
done
echo "${version}.$(( last + 1 ))"
