#!/usr/bin/env bash
# check-suite-accounting.sh, every repl suite must be TRACKED in git or REGISTERED with a reason.
#
# An untracked, unregistered suite is invisible TWICE: absent from git, absent from every registry. It runs
# in the gate, so it can fail forever without anyone seeing it, which is exactly how crash_yieldexpr hid two
# stale assertions until one happened to be run by hand (2026-09), and why 220 suites had to be force-added.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
# WHERE THE SUITES ARE is stated once, in _suites.sh (the gate is a tree; registries key bare names).
. "$ROOT/src/build/_suites.sh"
DIR="$SUITE_ROOT"
[ -d "$DIR" ] || { echo "no $DIR, run this from the repository root" >&2; exit 2; }

is_tracked() { git ls-files --error-unmatch "$1" >/dev/null 2>&1; }

# Same matching rules as the runner: comment stripped, first field, globs allowed.
is_registered() {
  local base="$1" reg line entry pattern
  for reg in src/build/repl-test-skip.txt src/build/repl-test-known-fail.txt; do
    [ -f "$reg" ] || continue
    while IFS= read -r line; do
      entry=$(printf '%s' "$line" | sed 's/#.*//' | sed 's/[[:space:]]*$//')
      [ -z "$entry" ] && continue
      # shellcheck disable=SC2086 # deliberate: split into pattern + reason
      set -- $entry
      pattern="$1"
      case "$base" in $pattern) return 0 ;; esac
    done < "$reg"
  done
  return 1
}

missing=""
total=0
dups=""
declare -A seen_names=()
for f in $(suite_files); do
  base=$(basename "$f")
  total=$((total + 1))
  # A basename is how every registry, baseline and report names a suite, so it MUST be unique across the
  # tree: two suites of one name would make a registry entry apply to whichever `find` reached first.
  if [ -n "${seen_names[$base]:-}" ]; then
    dups="$dups  $base: ${seen_names[$base]} and $f
"
  else
    seen_names[$base]="$f"
  fi
  if is_tracked "$f" || is_registered "$base"; then continue; fi
  missing="$missing  $base
"
done

if [ -n "$dups" ]; then
  echo "FAIL: two suites share a basename, so a registry entry cannot say which one it means:" >&2
  printf '%s' "$dups" >&2
  exit 1
fi

if [ -n "$missing" ]; then
  echo "FAIL: suites neither tracked nor registered, invisible if they break:" >&2
  printf '%s' "$missing" >&2
  echo >&2
  echo "Fix: git add the file, or list it in src/build/repl-test-skip.txt / repl-test-known-fail.txt with a reason." >&2
  exit 1
fi
echo "ok: all $total suites are tracked or registered"
