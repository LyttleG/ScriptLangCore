#!/usr/bin/env bash
# dump-bytecode.sh, capture every suite's compiled bytecode, byte for byte, so the format consolidation
# can be proven byte-identical by diffing this artifact before and after.
#
# Why the code bytes and not a disassembly: a readable dump needs each instruction's operand LENGTH, which
# is exactly the knowledge this refactor is consolidating, a dump built on that knowledge could agree with
# itself and still be wrong. The bytes cannot.
#
# Why --dump-bytecode and not an existing export: every script-visible export path (Bytecode.ExportScript,
# Bytecode.ExportLibrary, the .export -bc dotcommand) exports the SESSION'S ALREADY-RUN state,
# _scriptTopLevel is filled during Interpret, so it only sees what a run produced. Every suite here ends in
# exit(), which aborts such an export outright, and the benchmark suites cost minutes to run. --dump-bytecode
# parses, performs the same PrecompileFunctions step the CLI does, and prints the compiled declarations
# without ever executing the suite: fast, side-effect free, and reproducible.
#
# STREAMS ARE NOT MERGED. An earlier version of this script did `2>&1 | grep -v '<timestamped log>'`, and
# that lost lines: compiler warnings arrive on stderr while the dump writes to stdout, so a warning landing
# mid-line produced one merged line which the filter then deleted, making a function appear to be compiled
# in one sweep and not another, with no engine cause (CanVmHandle is a pure statement-shape check, and 186
# isolated dumps of the suite that flapped never reproduced it). stdout goes to the artifact, stderr to a
# temp file whose one deterministic coverage line is re-emitted as its own line. Verify any change to this
# script by sweeping twice and diffing.
#
# THE PROGRAM BODY IS COVERED TOO (`--program`). Every file's top level compiles as ONE body, the entry a
# bundle carries, and `DumpBytecode` verifies it behind that flag while nothing used to pass it: this sweep
# proved `refused: 0` for FUNCTIONS ONLY, so a verifier rule that falsely rejected an entry body was visible
# only where something LOADED such a bundle. Measured before the flag (2026-10-17, all 507 suites dumped a
# second time with it): 416 program bodies compiled, 37 declined, ZERO refused, so the corpus was clean and
# the gap was in the instrument. It also strengthens this artifact as a baseline, because the entry's own
# code and tables are now in it. Each refusal is written to the artifact as a `--- [dump-bytecode] REFUSED`
# line, with its reason, instead of only the exit code.
#
# Compare with `diff -a` (or --text): some suites carry a constant containing a NUL byte, so plain diff
# reports "Binary file ... matches" and compares nothing.
#
# AND THE PER-SUITE PROGRAM-BODY VERDICT IS A GATE OF ITS OWN (`src/build/program-body-baseline.txt`). Every suite
# records `compiled` or `declined` for its top level, and a suite that MOVES from compiled to declined is
# reported as a REGRESSION, because that is a body silently stopping compiling: it happened here while three
# checks were added to one suite, written with a top-level `foreach` over a CALL, which the statement-carrier
# rule declines. Nothing else noticed, since a declined top level still runs (each statement falls back to the
# tree), so the suite passed while its entry stopped being bytecode. --record writes the baseline, which
# carries the exe it was taken with; the gate only compares rows where BOTH sides are known, so a suite that
# failed to dump is not read as a regression.
#
# Usage: bash src/build/dump-bytecode.sh [--record] [out-file] [exe] [name-filter]
#   --record   write the current result as the program-body baseline instead of comparing against it.
# Defaults to the AOT publish, the shipping target. ALWAYS compare dumps taken with the SAME exe: a
# Debug-vs-AOT comparison would compare two binaries (the JIT path has a 24-frame cap, so even the compiled
# SET can differ).
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RECORD=0
ARGS=()
for a in "$@"; do
  case "$a" in
    --record) RECORD=1 ;;
    -h|--help) sed -n '2,46p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) ARGS+=("$a") ;;
  esac
done
OUT="${ARGS[0]:?usage: dump-bytecode.sh [--record] <out-file> [exe] [name-filter]}"
case "$OUT" in /*|[A-Za-z]:*) ;; *) OUT="$ROOT/$OUT" ;; esac
EXE_IN="${ARGS[1]:-bin/Release/net11.0/win-x64/publish/ScriptLangCore.exe}"
FILTER="${ARGS[2]:-}"
case "$EXE_IN" in /*|[A-Za-z]:*) EXE="$EXE_IN" ;; *) EXE="$ROOT/$EXE_IN" ;; esac
[ -x "$EXE" ] || {
  echo "no executable at $EXE" >&2
  echo "  the default is the AOT publish, which a "dotnet clean" removes:" >&2
  echo "    dotnet publish -c Release -v q --nologo" >&2
  echo "  or pass one: bash src/build/dump-bytecode.sh <out> bin/Debug/net11.0/ScriptLangCore.exe" >&2
  exit 2
}
# Always invoke from the repository root with a repository-relative suite path, so every path that lands in
# the artifact is identical between runs.
cd "$ROOT" || exit 2
# WHERE THE SUITES ARE is stated once, in _suites.sh (the gate is a tree; registries key bare names).
. "$ROOT/src/build/_suites.sh"
err="$(mktemp)"
tmpdir="$(mktemp -d)"
trap 'rm -f "$err"; rm -rf "$tmpdir"' EXIT
PROGBODIES="$tmpdir/progbodies.txt"
: > "$PROGBODIES"
if ! : > "$OUT" 2>/dev/null; then
  echo "cannot write the dump to '$OUT', does that directory exist?" >&2
  exit 2
fi
n=0; empty=0; refused=0; notrun=0
for suite in $(suite_files_matching "$FILTER"); do
  echo "=== $suite" >> "$OUT"
  # --program so the PROGRAM BODY (the file's top level compiled as the entry a bundle carries) is dumped
  # AND verified too. Without it this sweep proved `refused: 0` for FUNCTIONS only, which left the entry body
  # unverified corpus-wide: `DumpBytecode` verifies the program body behind its own flag, and nothing passed it,
  # so a verifier rule that falsely rejected a program body (item 1 added two, on a capture list and a compound
  # operand's const) would have shown up only where something LOADED such a bundle. It also makes the dump a
  # stronger baseline, since the entry's own code and tables are now in the artifact.
  timeout -k 5 60 "$EXE" --dump-bytecode "$suite" --program >> "$OUT" 2>"$err"
  rc=$?
  echo "--- exit=$rc" >> "$OUT"
  if [ "$rc" -eq 3 ]; then refused=$((refused + 1))
  elif [ "$rc" -ne 0 ]; then notrun=$((notrun + 1)); fi
  # EVERY refusal line, not only the summary: with --program a suite can be refused for its PROGRAM body while
  # its functions are fine, and that reason is what a reader needs (the exit code alone says "refused").
  while IFS= read -r line; do
    [ -n "$line" ] && echo "--- $line" >> "$OUT"
  done < <(grep -a '^\[dump-bytecode\] REFUSED' "$err")
  report="$(grep -a '^\[dump-bytecode\]' "$err" | grep -av 'REFUSED' | tail -1)"
  if [ -n "$report" ]; then
    echo "--- $report" >> "$OUT"
    grep -q "0 compiled function(s)" <<< "$report" && empty=$((empty + 1))
    # The per-suite verdict the baseline gate keys on. `unknown` covers a suite that was refused, timed out or
    # printed no verdict at all, and the comparison skips those rows so a bad run cannot read as a regression.
    case "$report" in
      *"program body: compiled"*) verdict=compiled ;;
      *"program body: declined"*) verdict=declined ;;
      *) verdict=unknown ;;
    esac
  else
    echo "--- no coverage line (the dump did not finish)" >> "$OUT"
    verdict=unknown
  fi
  printf '%s\t%s\n' "$(basename "$suite")" "$verdict" >> "$PROGBODIES"
  n=$((n + 1))
done
echo "dumped $n suites to $OUT (no compiled function: $empty, refused: $refused, did not run: $notrun)"

# ---- the program-body gate: did any suite's top level stop compiling?
PBBASELINE="$ROOT/src/build/program-body-baseline.txt"
compared=0; moveddown=0; movedup=0
if [ "$RECORD" -eq 1 ]; then
  # The exe is part of the record: the compiled SET can differ between a JIT build and the AOT publish (the JIT
  # path has a 24-frame cap), so a baseline taken with one must not be compared against the other.
  { echo "# exe: ${EXE#$ROOT/}"; sort -u "$PROGBODIES"; } > "$PBBASELINE"
  echo "recorded the program-body baseline: ${PBBASELINE#$ROOT/} ($n suite(s))"
elif [ -f "$PBBASELINE" ]; then
  awk -F'\t' '
    FILENAME == ARGV[1] { if ($1 !~ /^#/) was[$1] = $2; next }
    { now[$1] = $2 }
    END {
      n = 0
      for (s in now) {
        if (!(s in was)) continue
        if (was[s] == "unknown" || now[s] == "unknown") continue
        n++
        if (was[s] != now[s]) printf "%s\t%s\t%s\n", s, was[s], now[s]
      }
      print n > "/dev/stderr"
    }' "$PBBASELINE" "$PROGBODIES" > "$tmpdir/moved.txt" 2> "$tmpdir/compared.txt"
  # The count is how many rows were actually COMPARABLE, which a filtered sweep makes smaller than the baseline
  # (a suite the run did not reach has no row here, and its absence is not a move).
  compared=$(tr -d '\n' < "$tmpdir/compared.txt")
  moveddown=$(awk -F'\t' '$3 == "declined"' "$tmpdir/moved.txt" | wc -l | tr -d ' ')
  movedup=$(awk -F'\t' '$3 == "compiled"' "$tmpdir/moved.txt" | wc -l | tr -d ' ')
  if [ "$moveddown" -ne 0 ]; then
    echo "REGRESSED, these suites' program body used to compile and now declines:" >&2
    awk -F'\t' '$3 == "declined" { printf "  %s: %s -> %s\n", $1, $2, $3 }' "$tmpdir/moved.txt" >&2
    echo "  a declined top level still RUNS (each statement falls back to the tree), so no suite fails on it," >&2
    echo "  which is why this gate exists. Read the reason with: --dump-bytecode <suite> --program" >&2
  fi
  if [ "$movedup" -ne 0 ]; then
    echo "improved, these suites' program body compiles now (re-record with --record):"
    awk -F'\t' '$3 == "compiled" { printf "  %s: %s -> %s\n", $1, $2, $3 }' "$tmpdir/moved.txt"
  fi
  echo "program bodies: $compared compared, $moveddown regressed, $movedup improved"
else
  echo "no program-body baseline yet (src/build/program-body-baseline.txt): run this once with --record to create it"
fi
if [ "$refused" -ne 0 ] || [ "$notrun" -ne 0 ] || [ "$moveddown" -ne 0 ]; then
  if [ "$refused" -ne 0 ]; then
    echo "FAIL: $refused suite(s) had a body the verifier REFUSED, BytecodeFormat and the" >&2
  echo "      emitter disagree about an operand (that is how GET_PROP/SET_PROP were found)." >&2
  fi
  if [ "$notrun" -ne 0 ]; then
    echo "FAIL: $notrun suite(s) did not dump at all (exit 2 = exe/output problem, 124 = timeout)." >&2
  fi
  exit 1
fi
exit 0
