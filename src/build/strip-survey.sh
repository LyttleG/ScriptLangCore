#!/usr/bin/env bash
# strip-survey.sh, the `--no-source` frontier as ONE command: which suites refuse to be stripped, why, grouped
# by the engine's own phrase, and whether any of them REGRESSED against a recorded baseline.
#
# WHY IT EXISTS RATHER THAN A PASTE-AND-RUN SNIPPET. The frontier was a hand-written background job every time
# it was measured (the recipe in AGENTS.md), so the number in that file went stale between rounds and a change
# that made a suite STOP stripping could pass unnoticed, because the only thing anyone compared was the TOTAL.
# This script makes the whole frontier one command with a stable baseline, which is what every later item in the
# `--no-source` plan wanted for its verification step.
#
# WHY `--compile --no-source` AND NOT A RUN: the question is a COMPILE-time one (a body the compiler declines,
# or a declaration a bundle cannot carry), so nothing is executed, no suite can abort the survey, and a suite
# that costs minutes to run costs milliseconds here. It is the same choice `src/build/dump-bytecode.sh` makes, and
# for the same reason.
#
# A DECLINE IS COMPILE-TIME, so the JIT build and the AOT publish answer IDENTICALLY here (AGENTS.md records
# the A/B that established it), which is why the default exe below is simply the newest one available and why
# either leg is a valid survey.
#
# Usage: bash src/build/strip-survey.sh [--record] [out-file] [exe]
#   --record   write the current result as the baseline (src/build/strip-survey-baseline.txt) instead of
#              comparing against it. Do this AFTER a change that clears refusals, so the next run's
#              "regressed" list stays meaningful.
#   out-file   optional artifact (default: none, the summary goes to stdout)
#   exe        optional binary; the default is the newest of the AOT publish and the Debug build
#
# Exit codes: 0 = surveyed, nothing regressed; 1 = a suite that used to strip now refuses, or a suite did not
# run at all; 2 = no exe, or the out-file cannot be written.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

RECORD=0
ARGS=()
for a in "$@"; do
  case "$a" in
    --record) RECORD=1 ;;
    -h|--help) sed -n '2,26p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) ARGS+=("$a") ;;
  esac
done
OUT="${ARGS[0]:-}"
EXE_IN="${ARGS[1]:-}"

newest_exe() { ls -1dt "$@" 2>/dev/null | head -1; }
if [ -n "$EXE_IN" ]; then
  case "$EXE_IN" in /*|[A-Za-z]:*) EXE="$EXE_IN" ;; *) EXE="$ROOT/$EXE_IN" ;; esac
else
  # The framework directory is a GLOB, never a spelled net11.0: a TFM bump once left ten tools pointing at a
  # directory that no longer existed (AGENTS.md).
  EXE="$(cd "$ROOT" && newest_exe bin/Release/net*/win-x64/publish/ScriptLangCore.exe)"
  [ -n "$EXE" ] || EXE="$(cd "$ROOT" && newest_exe bin/Debug/net*/ScriptLangCore.exe)"
  [ -n "$EXE" ] || EXE="$ROOT/none"
fi
[ -x "$EXE" ] || {
  echo "no executable at $EXE" >&2
  echo "  build one (dotnet build -c Debug -v q --nologo) or pass one:" >&2
  echo "    bash src/build/strip-survey.sh '' bin/Debug/net11.0/ScriptLangCore.exe" >&2
  exit 2
}

cd "$ROOT" || exit 2
# WHERE THE SUITES ARE is stated once, in _suites.sh (the gate is a tree; registries key bare names).
. "$ROOT/src/build/_suites.sh"
if [ -n "$OUT" ]; then
  case "$OUT" in /*|[A-Za-z]:*) ;; *) OUT="$ROOT/$OUT" ;; esac
  if ! : > "$OUT" 2>/dev/null; then
    echo "cannot write the survey to '$OUT', does that directory exist?" >&2
    exit 2
  fi
fi

TMP="$(mktemp -d)"
ERR="$(mktemp)"
trap 'rm -rf "$TMP"; rm -f "$ERR"' EXIT

n=0; stripped=0; refused=0; notrun=0
: > "$TMP/refusals.txt"
for suite in $(suite_files); do
  name="$(basename "$suite")"
  bundle="$TMP/${name%.slc}.slcbc"
  out="$("$EXE" --compile "$suite" "$bundle" --no-source 2>&1)"
  rc=$?
  [ -f "$bundle" ] && rm -f "$bundle"
  n=$((n + 1))
  if [ "$rc" -eq 0 ]; then
    stripped=$((stripped + 1))
    continue
  fi
  # A refusal is exit 3 (src/build/dump-bytecode.sh's own contract); anything else is a run that did not finish.
  if [ "$rc" -eq 3 ] || [ "$rc" -eq 1 ]; then
    refused=$((refused + 1))
    reason="$(printf '%s' "$out" | grep -a -o 'cannot strip the source:.*' | head -1)"
    [ -n "$reason" ] || reason="$(printf '%s' "$out" | grep -a -o 'the top level did not compile.*' | head -1)"
    [ -n "$reason" ] || reason="$(printf '%s' "$out" | grep -a -o 'ERROR.*' | head -1)"
    [ -n "$reason" ] || reason="(refused, no phrase captured: exit $rc)"
    printf '%s\t%s\n' "$name" "$reason" >> "$TMP/refusals.txt"
  else
    notrun=$((notrun + 1))
    printf '%s\t(exit %s, did not compile at all)\n' "$name" "$rc" >> "$TMP/refusals.txt"
  fi
done

# The phrase is normalized into the FAMILY it belongs to, because the engine's message names the symbol it
# refused (a class, a list of member names) and the line, none of which changes the family. The rules are
# ordered specific-to-general and each one is anchored on wording the engine actually prints; anything
# unmatched keeps its raw head, so a NEW message shape shows up as its own row rather than being filed away
# under a wrong family. This is DISPLAY only: the baseline keys on the suite NAME, so a family rule that
# drifts cannot hide a regression.
phrase_of() {
  sed -e 's/.*while compiling a \([A-Za-z]*\) at line [0-9]*.*/the top level did not compile: a \1/' \
      -e 's/.*carries no body for .*/a coroutine or tree-only member whose body cannot be carried/' \
      -e 's/.*carries no declaration for the generic extension block.*/no declaration for a generic extension block/' \
      -e 's/.*carries no declaration for the generic class.*/no declaration for a generic class/' \
      -e 's/.*carries no declaration for a constructor of .*/no declaration for a constructor whose body the compiler did not take/' \
      -e 's/.*carries no declaration for the member .*/no declaration for a member whose body the compiler did not take/' \
      -e 's/.*carries no declaration for .*which a body.*/a name a carried body calls, which the bundle does not carry/' \
      -e "s/.*cannot run a body that loads .*a namespace block's.*/a namespace block's effect has no compiled form/" \
      -e 's/.*cannot run a body that loads .*/a body that loads a name the VM has no value for/' \
      -e 's/^(exit [0-9]*, did not compile at all)$/did not compile at all/' \
    | sed -e 's/[[:space:]]\+/ /g' -e 's/^ *//' -e 's/ *$//'
}
BASELINE="$ROOT/src/build/strip-survey-baseline.txt"
if [ "$RECORD" -eq 1 ]; then
  sort "$TMP/refusals.txt" > "$BASELINE"
fi

{
  echo "── exe: ${EXE#$ROOT/}"
  echo "── refusing suites ($refused):"
  while IFS="$(printf '\t')" read -r name reason; do
    [ -n "$name" ] || continue
    printf '   %-40s %s\n' "$name" "$(printf '%s' "$reason" | phrase_of | cut -c1-96)"
  done < "$TMP/refusals.txt"
  echo "── grouped by the engine's phrase:"
  cut -f2- "$TMP/refusals.txt" | phrase_of | sort | uniq -c | sort -rn | while read -r count phrase; do
    printf '   %3s  %s\n' "$count" "$(printf '%s' "$phrase" | cut -c1-96)"
  done
} | tee ${OUT:+"$OUT"}

if [ "$RECORD" -eq 1 ]; then
  echo "recorded the baseline: ${BASELINE#$ROOT/} ($refused suite(s) refusing)"
fi

regressed=0
if [ "$RECORD" -eq 0 ] && [ -f "$BASELINE" ]; then
  cut -f1 "$BASELINE" | sort > "$TMP/was.txt"
  cut -f1 "$TMP/refusals.txt" | sort > "$TMP/is.txt"
  new="$(comm -13 "$TMP/was.txt" "$TMP/is.txt")"
  gone="$(comm -23 "$TMP/was.txt" "$TMP/is.txt")"
  if [ -n "$new" ]; then
    regressed=1
    echo "REGRESSED, these suites used to strip and now refuse:" >&2
    printf '%s\n' "$new" | sed 's/^/  /' >&2
  fi
  if [ -n "$gone" ]; then
    echo "cleared, these suites strip now (re-record the baseline with --record):"
    printf '%s\n' "$gone" | sed 's/^/  /'
  fi
  [ -n "$new" ] || [ -z "$gone" ] || echo "(a cleared list means the baseline is now behind)"
elif [ "$RECORD" -eq 0 ]; then
  echo "no baseline yet (src/build/strip-survey-baseline.txt): run this once with --record to create it"
fi

echo "surveyed $n suites: stripped $stripped, refused $refused, did not run: $notrun"
if [ "$regressed" -ne 0 ] || [ "$notrun" -ne 0 ]; then
  exit 1
fi
exit 0
