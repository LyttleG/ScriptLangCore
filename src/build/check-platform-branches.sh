#!/usr/bin/env bash
# check-platform-branches.sh, every platform branch must say what it is and why.
#
# Why: RuntimeFeature.IsDynamicCodeSupported branches make JIT and NativeAOT behave differently. One of them
# was a byte-guard that only ran on AOT, so a hazard it was meant to prevent was silently unguarded on JIT,
# the bug this whole thread started from. Nothing enforced an explanation, so nothing stopped the next one.
#
# Rule: a non-comment use of IsDynamicCodeSupported must carry a marker on its line or the line above:
#     // platform: <limit|capability-guard|diagnostic>, <reason>
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
[ -d src ] || { echo "no src/ here, run this from the repository root" >&2; exit 2; }

missing=""
while IFS=: read -r file line _; do
  [ -z "$file" ] && continue
  this=$(sed -n "${line}p" "$file")
  prev=$(sed -n "$((line - 1))p" "$file")
  # A comment that merely mentions the API is not a branch.
  case "$this" in *//*IsDynamicCodeSupported*) 
    stripped=$(printf '%s' "$this" | sed 's|//.*||')
    case "$stripped" in *IsDynamicCodeSupported*) : ;; *) continue ;; esac ;;
  esac
  case "$this$prev" in
    *"// platform: limit"*|*"// platform: capability-guard"*|*"// platform: diagnostic"*) continue ;;
  esac
  missing="$missing  $file:$line
"
done < <(grep -rn "IsDynamicCodeSupported" src/ --include=*.cs)

if [ -n "$missing" ]; then
  echo "FAIL: platform branches with no marker (kind + reason):" >&2
  printf '%s' "$missing" >&2
  echo >&2
  echo "Fix: add '// platform: <limit|capability-guard|diagnostic>, <reason>' on the site or the line above." >&2
  exit 1
fi
echo "ok: every platform branch states its kind and reason"
