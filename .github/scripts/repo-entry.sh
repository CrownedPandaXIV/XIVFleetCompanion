#!/usr/bin/env bash
# Prints the Dalamud plugin repository list (repo.json) for one version: the manifest the
# build writes next to latest.zip, plus the download links, icon, update time and the
# version's CHANGELOG.md section. Fails if the manifest is missing or is for another version.
#
# Usage: repo-entry.sh <manifest.json> <version> <download-url>      (needs jq)
set -euo pipefail

manifest="${1:?manifest required}"
version="${2:?version required}"
url="${3:?download url required}"
icon="https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion/master/Data/submarine.png"

notes=""
if [ -f CHANGELOG.md ]; then
  notes="$(awk -v v="$version" '
    /^## / { if (found) exit; if ($2 == v) found = 1; next }
    found { print }
  ' CHANGELOG.md | sed '/./,$!d')"
fi

# The manifest can start with a byte order mark, which jq does not accept.
sed '1s/^\xEF\xBB\xBF//' "$manifest" | jq \
  --arg v "$version" --arg url "$url" --arg icon "$icon" --arg notes "$notes" \
  --argjson now "$(date +%s)" '
  if (.InternalName // "") == "" or .DalamudApiLevel == null then error("not a plugin manifest")
  elif ((.AssemblyVersion // "") | startswith($v)) | not then error("manifest is for \(.AssemblyVersion), not \($v)")
  else [ . + {
    DownloadLinkInstall: $url, DownloadLinkTesting: $url, DownloadLinkUpdate: $url,
    IconUrl: $icon, LastUpdate: $now, Changelog: $notes,
    IsHide: false, IsTestingExclusive: false
  } ] end'
