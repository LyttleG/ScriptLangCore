#!/usr/bin/env bash
# check-dot-commands.sh, the dot-command surface is ONE table and TWO dispatch halves that never overlap.
#
# Why this exists. The surface was dispatched TWICE: `Program.Repl.cs` called `Repl.DotCommands.Handle` first and
# fell back to `Program.HandleDotCommand` only when it answered false, while `DotCommands.Handle` returned true on
# every path of the commands it owned. So six arms of the session dispatcher were DEAD (`.clear`/`.cls`, `.gc`,
# `.history`, `.edit`, `.help`, `.vars`, about 300 of its 780 lines) and nobody could see it: nothing in the tree
# compared the two dispatchers. Worse, the dead `.reset` arm was the one that implemented the DOCUMENTED behaviour
# (a fresh interpreter for `hard`, `keepGlobals: false` for `all`, the refusal when an embedding host owns the
# interpreter), while the reachable arm ran the same soft reset in both branches, so `.reset hard` never reached
# the code the docs describe and `.reset` never cleared globals.
#
# The dead arms are deleted and `Program.DispatchDotCommand` is now the ONE entry point, so the rule that keeps it
# that way is checkable. Four ways to break it, and each one is silent without this check:
#
#   1. A NAME IN BOTH DISPATCHERS. The first dispatcher wins, so the second copy is unreachable by construction.
#   2. A NAME DISPATCHED BUT MISSING FROM `CommandHelp`. That table is what `.help` prints AND what
#      `IsKnownCommand` answers, and the file stripper uses `IsKnownCommand` to decide whether a column-1 dot line
#      in a SCRIPT is a directive to delete or code to keep. A command implemented in one place and not the other
#      therefore changes how scripts are stripped, which is a data-loss shape (see dot_command_strip_test.slc).
#   3. A ROW IN `CommandHelp` WITH NO ARM. The help text then promises what nothing answers.
#   4. A COMMAND NO SUITE DRIVES. `assets/language/repl/dot_command_surface_test.slc` drives every documented
#      command in a CHILD REPL and pins the three `.reset` modes and the alias bridge. Its list is held to the
#      dispatchers here, in both directions, so a new command cannot ship without a driver and a stale driver
#      cannot outlive its command.
#
# How: the arms are the `case ".name":` labels of each dispatcher (the nested sub-switches carry bare `"word"`
# labels, so the leading dot is the discriminator), the registry is the `[".name"] =` keys of `CommandHelp`, and
# the drivers are the `surface.Add(".name…")` lines of the suite. Then the five sets are compared. Plus the two
# structural facts the single entry point rests on: `DotCommands.Handle` has exactly ONE caller (the entry point),
# and the REPL calls the entry point rather than either dispatcher.
#
# Usage: bash src/build/check-dot-commands.sh
# Exit: 0 all hold, 1 a violation (named, with the offending commands), 2 the files are not where they should be.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

PROG=src/Program.DotCommands.cs
REPL=src/Repl/DotCommands.cs
REPL_CALLER=src/Program.Repl.cs
SUITE=assets/language/repl/run/dot_command_surface_test.slc

for f in "$PROG" "$REPL" "$REPL_CALLER" "$SUITE"; do
  [ -f "$f" ] || { echo "FAIL: no such file: $f (the surface moved, and this check was not told)" >&2; exit 2; }
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# A command NAME always starts with a dot; the sub-action labels (`"collect"`, `"trace"`) do not, which is what
# keeps `.gc collect` from reading as a command of its own.
arms_of() { grep -o 'case "\.[A-Za-z0-9]*":' "$1" | sed 's/^case "//; s/":$//' | sort -u; }

arms_of "$PROG" > "$TMP/prog"
arms_of "$REPL" > "$TMP/repl"
sort -u "$TMP/prog" "$TMP/repl" > "$TMP/handled"
grep -o '^[[:space:]]*\["\.[A-Za-z0-9]*"\]' "$REPL" | tr -d ' []"' | sort -u > "$TMP/table"
grep -o 'surface\.Add("\.[A-Za-z0-9]*' "$SUITE" | sed 's/^surface\.Add("//' | sort -u > "$TMP/suite"

FAILED=0
report() { # report <what> <set-file> <why>
  [ -s "$2" ] || return 0
  echo "FAIL: $1" >&2
  echo "  $3" >&2
  sed 's/^/    /' "$2" >&2
  FAILED=1
}

comm -12 "$TMP/prog" "$TMP/repl" > "$TMP/both"
report "commanded by BOTH dispatchers (the first one wins, so the other copy can never run)" "$TMP/both" \
  "keep the arm in the dispatcher that owns what the command needs, and delete the other"

comm -23 "$TMP/handled" "$TMP/table" > "$TMP/unlisted"
report "dispatched but missing from CommandHelp (.help will not list it, and IsKnownCommand will not know it)" \
  "$TMP/unlisted" "add a row in $REPL, the table .help prints and the file stripper reads"

comm -13 "$TMP/handled" "$TMP/table" > "$TMP/ghost"
report "listed in CommandHelp with no arm in either dispatcher (.help promises what nothing answers)" "$TMP/ghost" \
  "either implement it or delete the row"

comm -23 "$TMP/handled" "$TMP/suite" > "$TMP/undriven"
report "dispatched but never driven by $SUITE" "$TMP/undriven" \
  "add one surface.Add(\".name ...\") line to the suite's sweep, so the command is exercised in a child REPL"

comm -13 "$TMP/handled" "$TMP/suite" > "$TMP/orphan"
report "driven by $SUITE but handled by no dispatcher (a stale driver vouching for nothing)" "$TMP/orphan" \
  "remove the line, or restore the command it drives"

# The single entry point, structurally: exactly one caller of the interpreter-side dispatcher, and the REPL goes
# through the entry point. A second caller is how the two-dispatcher split grew back the first time.
CALLERS=$(grep -rl 'DotCommands\.Handle(' src --include=*.cs | sort -u)
NCALL=$(printf '%s\n' "$CALLERS" | grep -c .)
if [ "$NCALL" -ne 1 ] || [ "$CALLERS" != "src/Program.DotCommands.cs" ]; then
  echo "FAIL: Repl.DotCommands.Handle must be called from Program.DispatchDotCommand and nowhere else" >&2
  printf '%s\n' "$CALLERS" | sed 's/^/    caller: /' >&2
  FAILED=1
fi
if ! grep -q 'DispatchDotCommand(trimmedLine, replHandle)' "$REPL_CALLER"; then
  echo "FAIL: $REPL_CALLER does not dispatch through DispatchDotCommand (the one entry point)" >&2
  FAILED=1
fi
if grep -q 'HandleDotCommand(' "$REPL_CALLER"; then
  echo "FAIL: $REPL_CALLER calls HandleDotCommand directly, bypassing the one entry point" >&2
  FAILED=1
fi

[ "$FAILED" -eq 0 ] || exit 1

echo "ok: $(grep -c . "$TMP/handled") commands, one table, no name in both dispatchers, every one documented and driven"
