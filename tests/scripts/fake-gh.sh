#!/usr/bin/env bash
# A stand-in for the gh CLI, for tests/scripts/release-scripts.sh: keeps releases and the Releases repo's files in
# $FAKE_GH (a folder), so the publishing scripts can be run without GitHub. Every call is logged to $FAKE_GH/calls.
set -euo pipefail
s="${FAKE_GH:?FAKE_GH must be set}"
echo "$*" >> "$s/calls"
jqf=""; args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --jq) jqf="$2"; shift 2 ;;
    *) args+=("$1"); shift ;;
  esac
done
set -- "${args[@]}"
out() { if [ -n "$jqf" ]; then jq -r "$jqf"; else cat; fi; }
case "$1 $2" in
  "release view")
    d="$s/releases/$3"; [ -d "$d" ] || { echo "release not found" >&2; exit 1; }
    if [ "${4:-}" = "--json" ]; then
      jq -n --rawfile body "$d/notes" --arg assets "$(ls "$d/assets" 2>/dev/null || true)" \
        '{body: ($body | rtrimstr("\n")), assets: ($assets | split("\n") | map(select(. != "")) | map({name: .}))}' | out
    fi ;;
  "release create")
    d="$s/releases/$3"; mkdir -p "$d/assets"; shift 3; notes=""
    while [ $# -gt 0 ]; do case "$1" in --notes) notes="$2"; shift 2 ;; *) shift ;; esac; done
    printf '%s\n' "$notes" > "$d/notes" ;;
  "release edit")
    d="$s/releases/$3"; shift 3
    while [ $# -gt 0 ]; do case "$1" in --notes) printf '%s\n' "$2" > "$d/notes"; shift 2 ;; *) shift ;; esac; done ;;
  "release upload")
    cp "$4" "$s/releases/$3/assets/$(basename "$4")" ;;
  "release delete-asset")
    rm "$s/releases/$3/assets/$4" ;;
  *)
    [ "$1" = "api" ] || { echo "fake gh: no $1 $2" >&2; exit 2; }
    shift; method=GET; input=""; raw=false; path=""
    while [ $# -gt 0 ]; do
      case "$1" in
        -X) method="$2"; shift 2 ;;
        --input) input="$2"; shift 2 ;;
        -H) [ "$2" = "Accept: application/vnd.github.raw" ] && raw=true; shift 2 ;;
        --silent) shift ;;
        -f) shift 2 ;;
        *) path="$1"; shift ;;
      esac
    done
    file="$s/files/${path##*/contents/}"
    if [ "$method" = PUT ]; then
      want="$(jq -r '.sha // ""' "$input")"
      have="$( [ -f "$file" ] && sha1sum < "$file" | cut -c1-40 || true)"
      [ "$want" = "$have" ] || { echo "gh: sha does not match (HTTP 409)" >&2; exit 1; }
      mkdir -p "$(dirname "$file")"; jq -r .content "$input" | base64 -d > "$file"
    else
      [ -f "$s/fail-read" ] && { echo "gh: Server Error (HTTP 500)" >&2; exit 1; }
      [ -f "$file" ] || { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
      if $raw; then cat "$file"; else jq -n --arg c "$(base64 -w0 < "$file")" --arg sha "$(sha1sum < "$file" | cut -c1-40)" '{content: $c, sha: $sha}' | out; fi
    fi ;;
esac
