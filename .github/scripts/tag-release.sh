#!/usr/bin/env bash
# Creates the tag and GitHub release for a version if it does not exist yet, using the
# matching section of CHANGELOG.md as the release notes. If the release already exists
# but has an empty description, the notes are filled in. Never changes code.
#
# Usage: tag-release.sh <version> <commit-sha>      (needs the `gh` CLI and GH_TOKEN)
set -euo pipefail

version="${1:?version required}"
sha="${2:?commit sha required}"
tag="v${version}"

# Section of CHANGELOG.md that starts with "## <version>" up to the next "## " heading.
notes=""
if [ -f CHANGELOG.md ]; then
  notes="$(awk -v v="$version" '
    /^## / { if (found) exit; if ($2 == v) found = 1; next }
    found { print }
  ' CHANGELOG.md | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}' | sed '/./,$!d')"
fi
if [ -z "$notes" ]; then
  notes="Version ${version}"
fi

if gh release view "$tag" >/dev/null 2>&1; then
  body="$(gh release view "$tag" --json body --jq '.body')"
  if [ -z "$body" ]; then
    gh release edit "$tag" --notes "$notes"
    echo "Filled in the empty description of ${tag}."
  else
    echo "${tag} is already released; nothing to do."
  fi
  exit 0
fi

gh release create "$tag" --target "$sha" --title "$tag" --notes "$notes"
echo "Created ${tag} at ${sha}."
