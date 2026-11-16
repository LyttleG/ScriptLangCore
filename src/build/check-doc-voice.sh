#!/usr/bin/env bash
# check-doc-voice.sh, the language reference speaks to a SCRIPT AUTHOR, not to a maintainer.
#
# Why this exists: a page that ARGUES from evidence ("measured, not assumed") or narrates its own history reads as a
# lab notebook to someone who came for the rule. Those phrases arrive one at a time, each defensible on its own, and
# the drift is only visible in aggregate, the same shape check-suite-protocol guards for tests.
#
# Scope, deliberately narrow, and the first draft got this wrong: it also banned the shapes "— measured" and
# "measured:", and that flagged text that is NOT narration, "Performance, measured on the AOT build" and "Measured
# with <script>, phase 1a is …" both tell a reader WHERE a number came from, which is precisely what they need to
# judge it (the docs attribute their benchmarks to the AOT build on purpose). A number may carry its platform; what
# this bans is evidence used as an ARGUMENT, in the shapes below, each of which is unambiguous on its own. The
# evidence itself lives in AGENTS.md and in the bench suites, where a maintainer looks for it.
#
# Usage: bash src/build/check-doc-voice.sh [file]  , the argument exists so the rule can be tested against a known-bad
# copy, the way check-doc-structure.sh can.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
FILE="${1:-docs/language/index.html}"
[ -f "$FILE" ] || { echo "FAIL: no such file: $FILE" >&2; exit 1; }

BAD=0
check_phrase() {
  local phrase="$1"
  local hits
  hits=$(grep -c -F -- "$phrase" "$FILE" || true)
  if [ "$hits" != "0" ]; then
    printf '  %s: %s occurrence(s) of a maintainer-voice phrase: %s\n' "$FILE" "$hits" "$phrase" >&2
    BAD=1
  fi
}

check_phrase "measured, not assumed"
check_phrase "a measured trap"
check_phrase "measured, not a style choice"
check_phrase "Measured on the Excel layer"
check_phrase "measured with cdb"
check_phrase "(measured)"
check_phrase "measured:"

if [ "$BAD" -eq 0 ]; then
  echo "ok: docs/language/index.html speaks to a script author (no argument-from-evidence phrases)"
fi
exit "$BAD"
