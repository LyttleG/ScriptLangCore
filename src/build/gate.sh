#!/usr/bin/env bash
# gate.sh, run the repl gate with a heartbeat and a stall watchdog.
#
# Why a wrapper rather than a change inside run-repl-tests.sh: the failure actually observed was the RUNNER
# itself blocked in its own logic for 17+ minutes with no child process alive. timeout(1) guards each suite
# process, so the guard was in place and the stall still happened, outside its reach. Nothing inside a stuck
# bash can report its own stuckness, so the watchdog sits outside it.
#
# Usage: bash src/build/gate.sh [--stall SECONDS] [--log PATH] [-- <runner args...>]
# Defaults: --stall 120, --log a temp file, runner args --build aot-first.
# Exit: the runner's exit code, or 3 when a stall was detected and the run was killed.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

STALL=120
LOG="$(mktemp)"
RUNNER_ARGS=(--build aot-first)
while [ $# -gt 0 ]; do
  case "$1" in
    --stall) STALL="${2:-120}"; shift 2 ;;
    --log)   LOG="${2:-}"; shift 2 ;;
    --)      shift; RUNNER_ARGS=("$@"); break ;;
    *)       RUNNER_ARGS+=("$1"); shift ;;
  esac
done

# SLC_GATE_RUNNER exists so the watchdog itself can be tested against a fake runner that stalls on purpose:
# a watchdog you cannot make fire is a watchdog you cannot trust. Defaults to the real gate.
RUNNER_CMD="${SLC_GATE_RUNNER:-bash src/build/run-repl-tests.sh}"
# shellcheck disable=SC2086 # deliberate: RUNNER_CMD is a command plus its arguments
$RUNNER_CMD "${RUNNER_ARGS[@]}" > "$LOG" 2>&1 &
pid=$!

last_size=0
last_change=$SECONDS
exit_code=""
while :; do
  if ! kill -0 "$pid" 2>/dev/null; then
    wait "$pid"
    exit_code=$?
    break
  fi
  size=$(wc -c < "$LOG" 2>/dev/null || echo 0)
  if [ "$size" != "$last_size" ]; then
    last_size="$size"
    last_change=$SECONDS
    tail -1 "$LOG" | cut -c1-100
  elif [ $((SECONDS - last_change)) -ge "$STALL" ]; then
    echo >&2
    echo "WEDGE: no gate output for ${STALL}s. Last lines were:" >&2
    tail -2 "$LOG" | sed 's/^/  /' >&2
    echo "  (a suite budget cannot cover this: timeout(1) guards the suite process, not the runner)" >&2
    echo "  killing the run; the full log is kept at $LOG" >&2
    kill "$pid" 2>/dev/null
    sleep 2
    kill -9 "$pid" 2>/dev/null
    exit 3
  fi
  sleep 5
done

echo
echo "gate finished with exit code ${exit_code}; full log at $LOG"
tail -12 "$LOG"
exit "${exit_code:-1}"
