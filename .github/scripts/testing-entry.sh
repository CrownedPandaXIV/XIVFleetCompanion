#!/usr/bin/env bash
# Prints repo.json with a test build added to the plugin's entry: TestingAssemblyVersion, TestingDalamudApiLevel and
# DownloadLinkTesting. The normal version (AssemblyVersion, DownloadLinkInstall/Update, Changelog) is unchanged, so
# only players who opted in to testing versions get the test build.
#
# Usage: testing-entry.sh <repo.json> <build-version> <download-url> <dalamud-api-level>      (needs jq)
set -euo pipefail
jq --arg v "${2:?build version required}" --arg url "${3:?url required}" --argjson api "${4:?api level required}" \
  --argjson now "$(date +%s)" '
  if (length != 1) or ((.[0].InternalName // "") == "") then error("not this plugin'"'"'s repo.json")
  else [ .[0] + { TestingAssemblyVersion: $v, TestingDalamudApiLevel: $api, DownloadLinkTesting: $url, LastUpdate: $now } ] end' "${1:?repo.json required}"
