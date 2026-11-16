#!/usr/bin/env bash
# run-misc-scripts.sh, run the maintainer's own scripts under assets/language/misc/ and report which ones fail.
#
# WHY IT EXISTS: those scripts are a REGRESSION CORPUS the maintainer runs by hand ("I use to run each time"),
# and they are not suites: they live outside SUITE_DIR, most only `print` (no Check/CheckSummary), and the
# directory is gitignored. So neither `src/build/run-repl-tests.sh` nor the strip survey ever ran them, and a real
# regression reached the maintainer rather than a check: `41_test_step1_delegate_types.slc` failed with
# "'Action`2' is a generic type: write its type arguments" after the CLR generic work, and nothing in the tree
# noticed. This runner is the missing half, and it deliberately does NOT need the scripts to become suites.
#
# A script PASSES when it exits 0 AND its whole output carries no failure line: a `[ERROR]` (the engine's own
# report, which is how the reported regression showed up), a `N passed, M failed` tally with M above 0, or
# `TESTS FAILED`. Those are the same signals the gate reads, minus the exit-code-only case, because a
# print-based script has no tally to read.
#
# A script that TIMES OUT is reported as such rather than as a failure: the corpus holds benchmarks, HTTP
# samples and two debugger demos that legitimately wait. Anything that must not be counted goes in
# src/build/misc-scripts-skip.txt as `name # reason`, which is this repository's pattern for a parked item, and a
# name there is REPORTED as skipped, so it cannot be forgotten silently.
#
# Usage: bash src/build/run-misc-scripts.sh [exe] [name-filter]
#   exe          optional binary; the default is the newest of the AOT publish and the Debug build
#   name-filter  optional substring, so one script can be run while working on it
# Exit codes: 0 = every script that ran is clean, 1 = at least one failed, 2 = no exe.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EXE_IN="${1:-}"
FILTER="${2:-}"
SECONDS_PER_SCRIPT="${MISC_TIMEOUT:-30}"

newest_exe() { ls -1dt "$@" 2>/dev/null | head -1; }
if [ -n "$EXE_IN" ]; then
  case "$EXE_IN" in /*|[A-Za-z]:*) EXE="$EXE_IN" ;; *) EXE="$ROOT/$EXE_IN" ;; esac
else
  # The AOT publish is the product, so it is the default; the Debug build is the fallback. A publish OLDER than
  # the tree is reported below, because a fix that is only in src/ would otherwise be read as a failing script
  # (measured: the delegate fix looked absent for exactly this reason).
  EXE="$(cd "$ROOT" && newest_exe bin/Release/net*/win-x64/publish/ScriptLangCore.exe)"
  [ -n "$EXE" ] || EXE="$(cd "$ROOT" && newest_exe bin/Debug/net*/ScriptLangCore.exe)"
  [ -n "$EXE" ] || EXE="$ROOT/none"
fi
[ -x "$EXE" ] || {
  echo "no executable at $EXE" >&2
  echo "  build one (dotnet build -c Debug -v q --nologo) or pass one as the first argument" >&2
  exit 2
}

cd "$ROOT" || exit 2
echo "exe: ${EXE#$ROOT/}"
NEWEST_SRC="$(ls -1t src/*.cs src/*/*.cs src/*/*/*.cs 2>/dev/null | head -1)"
if [ -n "$NEWEST_SRC" ] && [ "$NEWEST_SRC" -nt "$EXE" ]; then
  echo "WARNING: $NEWEST_SRC is NEWER than that binary, so a fix in src/ is not in it." >&2
  echo "         Republish (dotnet publish -c Release -v q --nologo) or pass the Debug exe." >&2
fi
SKIP="src/build/misc-scripts-skip.txt"
ERR="$(mktemp)"
trap 'rm -f "$ERR"' EXIT

is_skipped() {
  [ -f "$SKIP" ] || return 1
  grep -E "^[[:space:]]*$1([[:space:]]|$)" "$SKIP" 2>/dev/null | grep -qv '^[[:space:]]*$'
}

passed=0; failed=0; timedout=0; skipped=0; n=0
failed_names=""
# The corpus is a TREE by topic now (basics/, classes/, runtime/, ...), each group keeping the numbered order
# its author gave it, so discovery is one recursive find rather than a flat glob.
while IFS= read -r script; do
  name="$(basename "$script")"
  case "$name" in *"$FILTER"*) ;; *) continue ;; esac
  if is_skipped "$name"; then
    echo "  skip    $name"
    skipped=$((skipped + 1))
    continue
  fi
  n=$((n + 1))
  timeout -k 5 "$SECONDS_PER_SCRIPT" "$EXE" "$script" > "$ERR" 2>&1
  rc=$?
  verdict="pass"
  detail=""
  if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ]; then
    verdict="timeout"; timedout=$((timedout + 1))
    detail="no answer within ${SECONDS_PER_SCRIPT}s"
  else
    bad="$(grep -a -m1 -E '\[ERROR\]|TESTS? FAILED' "$ERR")"
    if [ -z "$bad" ]; then
      # A tally is only a failure when the FAILED count is above zero: "6 passed, 0 failed" is a clean run, and
      # reading it as a failure is what a first version of this rule did (measured: 45_web_load, treegen_probe).
      tally="$(grep -a -o -E '[0-9]+ passed, [0-9]+ failed' "$ERR" | tail -1)"
      if [ -n "$tally" ]; then
        failed_count="${tally#*passed, }"
        failed_count="${failed_count%% failed*}"
        if [ "$failed_count" -gt 0 ]; then bad="$tally"; fi
      fi
    fi
    if [ -n "$bad" ]; then
      verdict="FAIL"; failed=$((failed + 1))
      detail="$(printf '%s' "$bad" | sed 's/^\[[0-9:.]*\] *//')"
      failed_names="$failed_names$name\n"
    elif [ "$rc" -ne 0 ]; then
      verdict="FAIL"; failed=$((failed + 1))
      detail="exit $rc with no error line"
      failed_names="$failed_names$name\n"
    fi
  fi
  if [ "$verdict" = "pass" ]; then
    passed=$((passed + 1))
    printf '  ok      %s\n' "$name"
  else
    printf '  %-7s %-46s %s\n' "$verdict" "$name" "$(printf '%s' "$detail" | cut -c1-110)"
  fi
done < <(find assets/language/misc -type f -name '*.slc' | LC_ALL=C sort)

if [ "$n" -eq 0 ]; then
  echo "no scripts to run under assets/language/misc${FILTER:+ matching '$FILTER'}"
  echo "(that directory is gitignored: a fresh checkout has none of them, which is normal)"
  exit 0
fi

echo "ran $passed clean, $failed failed, $timedout timed out, $skipped skipped (of $((n + skipped)))"
if [ "$failed" -ne 0 ]; then
  echo "FAIL: these scripts are the maintainer's own regression corpus and must run clean:" >&2
  printf '%b' "$failed_names" | sed 's/^/  /' >&2
  exit 1
fi
exit 0
