#!/usr/bin/env bash
# check-rules-pins.sh, RULES.md cites suites as evidence, and a citation to a file that is gone (or is not
# tracked) is how a rule rots back.
#
# WHY IT EXISTS. RULES.md states each rule once and names the suite that PINS it. Two things go wrong with that
# over time, and both happened in one session: a row named a behaviour the engine no longer had (corrected by
# hand), and a pin can name a file that was renamed, deleted, or never added to git, in which case the rule is
# documented as pinned and nothing runs it. A suite that is not tracked is invisible to the gate for exactly
# the reason check-suite-accounting.sh exists, so this check asks the narrower question about RULES.md itself.
#
# WHAT IT CHECKS, for every `*.slc` the file names:
#   1. the file exists (`foo.slc` resolves under assets/language/repl, the suites' only home, and a path is
#      taken as written),
#   2. it is TRACKED by git, because an untracked suite is a pin nothing can run.
# It reports one `ok:` line with both counts and exits 1 with the names that failed.
#
# WHAT IT DOES NOT DO: it does not judge whether the suite still ASSERTS the rule. That is a reading, and this
# check exists to make the mechanical half free.

set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

RULES="RULES.md"
# WHERE THE SUITES ARE is stated once, in _suites.sh (the gate is a tree; a pin names a suite by bare name).
. "$ROOT/src/build/_suites.sh"

if [ ! -f "$RULES" ]; then
  echo "FAIL: $RULES is missing, so its pins cannot be checked"
  exit 1
fi

# Every `*.slc` the file names, once each, in the order it appears.
mapfile -t named < <(grep -oE '[A-Za-z0-9_./-]+\.slc' "$RULES" | awk '!seen[$0]++')

missing=()
untracked=()
for name in "${named[@]}"; do
  if [[ "$name" == */* ]]; then
    path="$name"
  else
    path="$(suite_path "$name")"
  fi
  if [ -z "$path" ] || [ ! -f "$path" ]; then
    missing+=("$name")
    continue
  fi
  if ! git ls-files --error-unmatch "$path" >/dev/null 2>&1; then
    untracked+=("$path")
  fi
done

if [ "${#missing[@]}" -gt 0 ] || [ "${#untracked[@]}" -gt 0 ]; then
  if [ "${#missing[@]}" -gt 0 ]; then
    echo "FAIL: RULES.md names suite(s) that do not exist:"
    printf '  %s\n' "${missing[@]}"
  fi
  if [ "${#untracked[@]}" -gt 0 ]; then
    echo "FAIL: RULES.md pins suite(s) that git does not track, so nothing runs them:"
    printf '  %s\n' "${untracked[@]}"
  fi
  echo "Fix: correct the name in RULES.md, or 'git add' the suite it means."
  exit 1
fi

echo "ok: every suite RULES.md names exists and is tracked (${#named[@]} name(s))"
