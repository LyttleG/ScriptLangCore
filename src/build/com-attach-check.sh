#!/usr/bin/env bash
# com-attach-check.sh, exercise the ATTACHED branch of the COM lifetime rules, reproducibly, with one command.
#
# The rule under test: an instance this process did NOT start is never reported and never touched. Its owner's
# Excel must survive a whole suite run.
#
# Why a tool and not a suite check: the branch needs a FOREIGN owner, a second process holding an Excel, and
# com_lifetime_test.slc reports its attached checks (13-14) as SKIPPED when the machine has no pre-existing
# instance. This makes that case reproducible, and asserts the three things that matter:
#   1. the suite actually RAN those checks (not skipped, never silently green),
#   2. the holder's instance SURVIVED the suite (nothing ended somebody else's Excel),
#   3. it went away when the HOLDER quit it, which is the script's job, never the engine's.
#
# usage: src/build/com-attach-check.sh [--exe PATH] [--hold SECONDS]
set -uo pipefail

EXE="bin/Release/net11.0/win-x64/publish/ScriptLangCore.exe"
HOLD=45
while [ $# -gt 0 ]; do
  case "$1" in
    --exe) EXE=$2; shift 2 ;;
    --hold) HOLD=$2; shift 2 ;;
    *) echo "unknown option: $1"; exit 2 ;;
  esac
done

ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT" || exit 2
case "$EXE" in
  /*|[A-Za-z]:[\\/]*) ABSEXE="$EXE" ;;
  *) ABSEXE="$ROOT/$EXE" ;;
esac
EXEDIR=$(cd "$(dirname "$ABSEXE")" && pwd)

count_excel() { tasklist 2>/dev/null | grep -c 'EXCEL.EXE' || true; }

echo "attach-check : a HOLDER process owns an Excel; the suite then runs AGAINST it"
echo "exe          : $ABSEXE"

# Preconditions, PRINTED (a stray Excel makes "survived" read the wrong process, and this check must never touch a
# user's own instance).
[ -f "$ABSEXE" ] || { echo "PRECONDITION FAILED: no executable at $ABSEXE (build it first)"; exit 2; }
if find src -name '*.cs' -newer "$ABSEXE" 2>/dev/null | head -1 | grep -q .; then
  echo "PRECONDITION FAILED: the executable is OLDER than src/ (republish, or pass --exe the one you mean)"; exit 2
fi
PRE=$(count_excel)
echo "precondition : EXCEL.EXE before = $PRE  (must be 0)"
if [ "$PRE" != "0" ]; then
  echo "REFUSING: quit your own Excel first, the holder's instance has to be the only one, or this check"
  echo "          cannot tell which instance survived."
  exit 2
fi

TMP=$(mktemp -d)
HOLDER="$TMP/holder.slc"
HOLD_MS=$((HOLD * 1000))
cat > "$HOLDER" <<HOLDER_SCRIPT
// Written at run time by src/build/com-attach-check.sh. Owns ONE Excel for a while, so another process can attach.
// Quitting it is THIS script's job: the engine only reports what was left running.
Com.Run(fun() {
    var app = Com.CreateObject("Excel.Application");
    app.Visible = false;
    print("holder: Excel up (" + app.Name + ")");
    sleep($HOLD_MS);
    app.Quit();
    print("holder: Quit() called");
});
print("holder: done");
HOLDER_SCRIPT

echo "holder       : starting (holds ~${HOLD}s)"
( cd "$EXEDIR" && "$ABSEXE" "$HOLDER" >"$TMP/holder.log" 2>&1 ) &
HOLDER_PID=$!

UP=0
WAITED=0
for _ in $(seq 1 40); do
  sleep 1
  WAITED=$((WAITED + 1))
  if [ "$(count_excel)" -ge 1 ]; then UP=1; break; fi
done
echo "holder       : Excel up = $UP after ${WAITED}s"
if [ "$UP" != "1" ]; then
  echo "PRECONDITION FAILED: the holder never started an Excel"
  sed 's/^/  holder: /' "$TMP/holder.log"
  kill $HOLDER_PID 2>/dev/null
  rm -rf "$TMP"
  exit 2
fi

BEFORE_SUITE=$(count_excel)
echo "suite        : running com_lifetime_test.slc against the held instance..."
SUITE_OUT="$TMP/suite.log"
( cd "$EXEDIR" && "$ABSEXE" "$ROOT/assets/language/repl/com/com_lifetime_test.slc" ) >"$SUITE_OUT" 2>&1
SUITE_RC=$?
AFTER_SUITE=$(count_excel)
echo "suite        : rc=$SUITE_RC, EXCEL.EXE $BEFORE_SUITE -> $AFTER_SUITE"
grep -E "0[0-9]?\. |1[0-9][a-z]?\. |ALL TESTS PASSED|TEST\(S\) FAILED" "$SUITE_OUT" | sed 's/^/  /'

echo "holder       : waiting for it to quit on its own (up to ${HOLD}s more)"
LEFT=$AFTER_SUITE
for _ in $(seq 1 $HOLD); do
  sleep 1
  LEFT=$(count_excel)
  [ "$LEFT" = "0" ] && break
done

fail=0
grep -q "ALL TESTS PASSED" "$SUITE_OUT" || { echo "FAIL: the suite did not pass"; fail=1; }
grep -qE "13\. an ATTACHED instance is never reported" "$SUITE_OUT" \
  || { echo "FAIL: check 13 did not RUN, the attached branch was skipped, not exercised"; fail=1; }
grep -qE "14\. \.\.\.and it is left running" "$SUITE_OUT" \
  || { echo "FAIL: check 14 did not RUN"; fail=1; }
[ "$AFTER_SUITE" -ge 1 ] || { echo "FAIL: the holder's Excel did NOT survive the suite (something ended it)"; fail=1; }
[ "$LEFT" = "0" ] || { echo "FAIL: EXCEL.EXE = $LEFT still running after the holder quit it"; fail=1; }

echo
echo "postcondition: EXCEL.EXE after the holder quit = $LEFT"
if [ "$fail" = "0" ]; then
  echo "VERDICT: PASS, the attached instance was never reported, never touched, and left when its OWNER quit it."
else
  echo "VERDICT: FAIL ($TMP kept for inspection)"
  exit 1
fi
rm -rf "$TMP"
exit 0
