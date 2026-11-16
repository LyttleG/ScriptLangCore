#!/usr/bin/env bash
# check-dup-friendly-maps.sh, guard against a SECOND copy of the friendly-exception mapping.
#
# Why this exists: the engine carried FOUR copies of the exception→message switch (TypeHelpers holds the
# canonical FriendlyExceptionMessage). Only the copy that a given input happens to reach matters, so a
# duplicate missing the newest case stays invisible until it leaks a raw CLR/Win32 message to a script,
# which is exactly how "DllNotFound_Windows, sqlite3, <localized>" reached one, and how
# "Arg_OutOfMemoryException" reached another before that. This turns "notice it in review" into a command.
#
# Looked for, in src/**/*.cs:
#   1. more than ONE switch tail of the form `_ => <something>.Message`. The canonical map owns exactly one;
#      any second one forwards a raw message, which is the arm that leaks.
#   3. a raw .Message INTERPOLATED into an error the script will see (ThrowException("…{ex.Message}")), which
#      is not a switch arm so checks 1-2 cannot see it. This is the shape of the bug that leaked three times,
#      including once in the dispatch catch written to fix the previous leak. Only the interpolation form is
#      flagged: passing a ScriptException's own (already friendly) message straight through is legitimate.
#
#   2. a SWITCH ARM that names an exception type and maps it to a string ("FileNotFoundException fnf => …")
#      anywhere OUTSIDE TypeHelpers.cs. Deliberately not a wording search: ordinary user-facing messages like
#      `Error: File not found: {path}` in the REPL/MCP/CLI are correct and would have made this guard noisy.
#
# Exit codes: 0 clean, 1 duplicates found (each printed as file:line:...), 2 not run from the repo root.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
[ -d src ] || { echo "no src/ here, run this from the repository root" >&2; exit 2; }
CANON="src/Interpreter/TypeHelpers.cs"
fail=0

# 1. raw-message switch tails (`_ => ex.Message`, `_ => e.Message`, …)
tails=$(grep -rnE '_ *=> *[A-Za-z_][A-Za-z0-9_]*\.Message' src/ --include=*.cs || true)
count=$(printf '%s\n' "$tails" | grep -c . || true)
if [ "$count" -gt 1 ]; then
  echo "FAIL: $count switch tails forward a raw exception Message, the friendly map must own the only one:" >&2
  printf '%s\n' "$tails" | sed 's/^/  /' >&2
  fail=1
elif [ "$count" -eq 1 ] && ! printf '%s\n' "$tails" | grep -q "^$CANON:"; then
  echo "FAIL: the single raw-message tail is NOT in $CANON, a copy has replaced the canonical map:" >&2
  printf '%s\n' "$tails" | sed 's/^/  /' >&2
  fail=1
fi

# 2. arms that belong to the canonical map only
copied=$(grep -rnE '(FileNotFound|DirectoryNotFound|UnauthorizedAccess|DivideByZero|Overflow|Format|NotImplemented|ArgumentOutOfRange|EntryPointNotFound|BadImageFormat)Exception[^;]*=>'   src/ --include=*.cs | grep -v "^$CANON:" || true)
if [ -n "$copied" ]; then
  echo "FAIL: a copy of the friendly map, an exception type mapped to a string belongs to $CANON only:" >&2
  printf '%s\n' "$copied" | sed 's/^/  /' >&2
  fail=1
fi

# 3. raw message interpolated into a script-visible error
bypass=$(grep -rnE '(ThrowException|RuntimeError|RuntimeErrorAt|ScriptErrorInfo|ErrorInfo)\(.*\$"[^"]*\{[A-Za-z_][A-Za-z0-9_.]*\.Message'   src/ --include=*.cs | grep -v "^$CANON:" || true)
if [ -n "$bypass" ]; then
  echo "FAIL: a raw exception Message is interpolated into a script-visible error:" >&2
  printf '%s
' "$bypass" | sed 's/^/  /' >&2
  fail=1
fi

if [ "$fail" -eq 0 ]; then
  echo "ok: one friendly exception map ($CANON), no copies, no interpolated raw messages"
  exit 0
fi
echo >&2
echo "Fix: have the offending site call FriendlyExceptionMessage(ex) instead of mapping messages itself." >&2
exit 1
