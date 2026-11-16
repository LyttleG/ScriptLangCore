#!/usr/bin/env bash
# scenario.sh, run one investigation/repro with its preconditions and postcondition CHECKED, its FULL output kept,
# and the exit code as the verdict.
#
# Written after a long session in which every wasted round came from one of three things: a STALE BINARY (a failed
# build leaves the previous exe in place), a FILTERED RUN (grep/tail hiding the evidence) and an UNCONTROLLED
# PRECONDITION (a stray Excel made an attach look like a create). So: assert the preconditions and PRINT them, keep
# the whole output on a durable path, check the postcondition after a settle, and let the exit code decide.
#
# usage: src/build/scenario.sh <name> <exe> <script> [--settle N] [--no-excel]
set -uo pipefail

NAME=${1:?usage: scenario.sh <name> <exe> <script> [--settle N] [--no-excel]}
shift
EXE=${1:?missing exe}
shift
SETTLE=18
NOEXCEL=0
ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --settle) SETTLE=$2; shift 2 ;;
    --no-excel) NOEXCEL=1; shift ;;
    *) ARGS+=("$1"); shift ;;
  esac
done

ROOT=$(cd "$(dirname "$0")/.." && pwd)
LOGDIR="$HOME/.reasonix-scenarios"
mkdir -p "$LOGDIR"
LOG="$LOGDIR/$NAME-$(date +%Y%m%d-%H%M%S).log"
fail=0

echo "scenario : $NAME"
echo "log      : $LOG"

if [ ! -f "$EXE" ]; then
  echo "  PRECONDITION FAILED: no executable at $EXE (build it first)"
  exit 2
fi

NEWER=$(find "$ROOT/src" -name '*.cs' -newer "$EXE" 2>/dev/null | head -3)
if [ -n "$NEWER" ]; then
  echo "  PRECONDITION FAILED: $EXE is OLDER than src/, a failed build leaves the previous exe, and this run would measure it:"
  echo "$NEWER" | sed 's/^/    /'
  exit 2
fi
echo "  precondition: exe is newer than src/    ok"

if [ "$NOEXCEL" = 0 ]; then
  PRE=$(tasklist 2>/dev/null | grep -c 'EXCEL.EXE' || true)
  echo "  precondition: EXCEL.EXE before = $PRE    (a stray one changes what GetOrCreate does)"
fi

( cd "$ROOT" && timeout 1800 "$EXE" "${ARGS[@]}" ) > "$LOG" 2>&1
RC=$?
echo "  exit     : $RC"
echo "  --- every line is in the log; here is the tail ---"
tail -30 "$LOG" | sed 's/^/    /'

if [ "$NOEXCEL" = 0 ]; then
  sleep "$SETTLE"
  POST=$(tasklist 2>/dev/null | grep -c 'EXCEL.EXE' || true)
  echo "  postcondition: EXCEL.EXE after ${SETTLE}s = $POST"
  if [ "$POST" != "0" ]; then
    echo "  POSTCONDITION FAILED: an EXCEL.EXE outlived the run"
    fail=1
  fi
fi

[ "$RC" != "0" ] && fail=1
echo "  VERDICT  : $([ $fail = 0 ] && echo PASS || echo FAIL)    log: $LOG"
exit $fail
