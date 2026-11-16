#!/usr/bin/env bash
# ab-check.sh, A/B a .slc between a PINNED "before" binary and the current build.
#
# Why this exists: comparing a change against the previous binary is the only thing that separates
# "my change is equivalent" from "my change is masked by something else", and the previous binary is
# exactly what the next `dotnet publish` (or an AOT checkpoint) overwrites, so the evidence vanishes at
# the moment it is needed. Nine hand-rolled comparisons in one working session lost their "before"
# binary to a republish; this pins a copy first.
#
#   src/build/ab-check.sh --record script.slc   # pin the CURRENT build as the baseline
#   ... make the change, publish/build ...
#   src/build/ab-check.sh script.slc            # run both, print both, say identical or DIFFERS
#
# The output is the evidence, not a verdict: a difference is usually the point of the change. Both runs
# go through the SAME temp copy of the script, so the only variable is the binary. `VmStats()` is
# appended when the script does not print it, so engine coverage is part of the record rather than an
# afterthought, half the confusions this tool exists for came from not saying which engine ran.
#
# --lax wraps the run in a REPL session (`.lax … .exit`); a plain script is run BY PATH. The temp copy keeps
# the .slc EXTENSION: without it the exe sees "no script argument" and starts the REPL, which waits for
# input instead of running the file, that is what hung this tool's first runs (found by tracing, not guessing).: the case mode is a REPL command, and a script
# cannot switch it, so a case-sensitivity claim cannot be checked from a plain file.
#
# Usage:
#   src/build/ab-check.sh --record <script>        # pin the current build; runs it once and shows the output
#   src/build/ab-check.sh <script>                 # compare (default timeout 120s per side)
#   src/build/ab-check.sh --lax <script>           # compare in .lax (case-insensitive) mode
#   src/build/ab-check.sh --exe <path> <script>    # A/B against a specific CURRENT binary
#   src/build/ab-check.sh --baseline <path> <script>   # A/B against a specific baseline
#   src/build/ab-check.sh --timeout 30 <script>
#
# Exit: 0 both ran, output identical · 1 both ran, output DIFFERS · 2 setup problem (no baseline, no
# binary, missing script), never a silent pass.

set -u
export PATH="/usr/bin:/bin:/usr/local/bin:$PATH"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ABDIR="$ROOT/bin/ab"
BASE="$ABDIR/before.exe"
META="$ABDIR/before.txt"
AOT="$ROOT/bin/Release/net11.0/win-x64/publish/ScriptLangCore.exe"
JIT="$ROOT/bin/Debug/net11.0/ScriptLangCore.exe"

RECORD=0 LAX=0 EXE="" BASELINE="" TIMEOUT=120 SCRIPT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --record)   RECORD=1; shift ;;
    --lax)      LAX=1; shift ;;
    --exe)      EXE="${2:-}"; shift 2 ;;
    --baseline) BASELINE="${2:-}"; shift 2 ;;
    --timeout)  TIMEOUT="${2:-}"; shift 2 ;;
    -h|--help)  sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*)         echo "ab-check: unknown option $1" >&2; exit 2 ;;
    *)          SCRIPT="$1"; shift ;;
  esac
done

[ -n "$SCRIPT" ] || { echo "ab-check: give a .slc (see --help)" >&2; exit 2; }
[ -f "$SCRIPT" ] || { echo "ab-check: no such script: $SCRIPT" >&2; exit 2; }
SCRIPT_ABS="$(cd "$(dirname "$SCRIPT")" && pwd)/$(basename "$SCRIPT")"

# The current build: the AOT publish, else the Debug apphost. (Same "newest is right for day-to-day"
# reasoning as run-repl-tests.sh, minus its --build flag: an A/B pins both sides explicitly anyway.)
if [ -z "$EXE" ]; then
  if [ -f "$AOT" ]; then EXE="$AOT"; elif [ -f "$JIT" ]; then EXE="$JIT"; else
    echo "ab-check: no binary, run 'dotnet publish -c Release' first" >&2; exit 2
  fi
fi
[ -f "$EXE" ] || { echo "ab-check: no such --exe: $EXE" >&2; exit 2; }
if [ -n "$BASELINE" ]; then BASE="$BASELINE"; fi

newest_src() { slc_engine_newest_source "$1" "$ROOT"; }
warn_stale() { # a stale artifact reports old behaviour: the runner REFUSES on it, this tool only says so
  local stale; stale="$(newest_src "$1")"
  [ -n "$stale" ] || return 0
  echo "ab-check: WARNING $2 is OLDER than $stale, it does not contain your change." >&2
  echo "ab-check:          publish/build first, or the 'after' side is a lie." >&2
}

mkdir -p "$ABDIR"

if [ "$RECORD" = 1 ]; then
  cp -f "$EXE" "$BASE"
  {
    echo "recorded:   $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo "from:       $EXE"
    echo "commit:     $(cd "$ROOT" && git rev-parse --short HEAD 2>/dev/null || echo '(not a repo)')"
    echo "size:       $(wc -c < "$BASE" | tr -d ' ') bytes"
  } > "$META"
  echo "ab-check: baseline pinned → $BASE"
  sed 's/^/           /' "$META"
  echo "ab-check: after a republish, compare with: src/build/ab-check.sh ${LAX:+--lax }$SCRIPT"
fi

[ -f "$BASE" ] || { echo "ab-check: no baseline, record one first: src/build/ab-check.sh --record $SCRIPT" >&2; exit 2; }

# One temp copy of the script per side, so the binary is the only difference; VmStats rides along when
# the script is silent about it.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
make_input() {
  local out="$1"
  : > "$out"   # TRUNCATE: both sides must get the SAME input (appending twice duplicated VmStats)
  if [ "$LAX" = 1 ]; then printf '.lax\n' >> "$out"; fi
  cat "$SCRIPT_ABS" >> "$out"
  grep -q 'VmStats' "$SCRIPT_ABS" || printf '\nprint(VmStats());\n' >> "$out"
  if [ "$LAX" = 1 ]; then printf '.exit\n' >> "$out"; fi
}
run_side() { # $1 = exe, $2 = label, $3 = output file
  local dir; dir="$(dirname "$1")"
  make_input "$WORK/probe.slc"
  # BY PATH, not on stdin: with no argument the exe starts the REPL, which does not exit on EOF, so feeding a
  # plain script through stdin HANGS this tool (measured on its first run). Only --lax wants the REPL, and it
  # ends with `exit`. The path is translated for the exe, because a /tmp path is not one it can open.
  local arg; arg="$(cygpath -w "$WORK/probe.slc" 2>/dev/null || echo "$WORK/probe.slc")"
  if [ "$LAX" = 1 ]; then
    ( cd "$dir" && timeout "$TIMEOUT" "./$(basename "$1")" < "$WORK/probe.slc" > "$3" 2>&1 )
  else
    ( cd "$dir" && timeout "$TIMEOUT" "./$(basename "$1")" "$arg" > "$3" 2>&1 )
  fi
  local rc=$?
  if [ $rc -eq 124 ]; then echo "ab-check: $2 TIMED OUT after ${TIMEOUT}s" >> "$3"; fi
  return 0
}

run_side "$BASE" "before" "$WORK/before.out"
if [ "$BASELINE" = "" ]; then warn_stale "$EXE" "the current build" ; fi
run_side "$EXE" "after" "$WORK/after.out"

echo "── before ($BASE) ──"; sed 's/^/  /' "$WORK/before.out"
echo "── after  ($EXE) ──"; sed 's/^/  /' "$WORK/after.out"
if diff -q "$WORK/before.out" "$WORK/after.out" >/dev/null; then
  echo "ab-check: IDENTICAL, the change is behaviour-neutral here (or masked; see AGENTS.md Notes)."
  exit 0
fi
echo "ab-check: DIFFERS"
diff -a "$WORK/before.out" "$WORK/after.out" | sed 's/^/  /'
exit 1
