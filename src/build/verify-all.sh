#!/usr/bin/env bash
# verify-all.sh, the repository's verification set in ONE command, with one verdict.
#
# WHY IT EXISTS. Every instrument below already existed and each answers a DIFFERENT question about the same
# tree, but nothing ran them together: which ones had been run was a memory rather than a check, so a round could
# be called verified while the two host-side harnesses, the bundle parity ledger or the maintainer's own corpus
# had never been touched. The costs of that are recorded in AGENTS.md, one entry per instrument, each written
# after a defect reached a hand rather than a check (a suite whose stripped BUNDLE answered differently, a
# plugin loader nobody re-ran, a client-side regression in the 121 script corpus).
#
# WHAT IT DOES NOT DO, deliberately:
#   * it never BUILDS. Every instrument REFUSES an artifact older than src/ (src/build/_artifacts.sh, one rule
#     shared by the gate, embed-check.sh, bundle-parity.sh and ab-check.sh), so this script measured against a
#     fresh build cannot measure a stale binary. Publish first (`compile_aot.bat`), or pass --exe.
#   * it never RECORDS a baseline. `--record` is a deliberate act that redefines a gate, so it stays a flag on
#     the instrument it belongs to.
#   * it never writes into the tree. Each step's output goes to a temp folder, except the transient artifacts an
#     instrument already owns (bundle-parity.sh writes `_parity_<suite>.slcbc` BESIDE each suite, which is what
#     makes a bundle resolve `./` the way its source does, and deletes it afterwards).
#   * it never short-circuits: every step runs, so one run reports every red boundary instead of the first.
#
# Usage:
#   bash src/build/verify-all.sh                  # everything, in the order that pays
#   bash src/build/verify-all.sh --list           # the steps, what each answers and the script behind it
#   bash src/build/verify-all.sh --plan           # the exact command line of every step, running nothing
#   bash src/build/verify-all.sh --skip suites    # everything except the suite gate
#   bash src/build/verify-all.sh --only invariants,format  # just those two steps, in the order they run
#   bash src/build/verify-all.sh --exe bin/Debug/net11.0/ScriptLangCore.exe
#
# Exit codes: 0 every step green, 1 at least one step failed, 2 the command line could not be used.
#
# COST, measured rather than guessed: `format` is about two minutes over the whole corpus and `parity` about thirty
# (both whole-corpus sweeps run a fresh process per suite), `suites` is the gate at ten to twenty depending on the
# platform set, `jitplugin` rebuilds a fixture and publishes a NativeAOT host (a couple of minutes), and the rest
# are seconds to a minute each. So the full run is the "before a commit that touches the engine" set, not the
# "while working" one: use --skip or --only for the inner loop.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

ONLY=""
SKIP=""
EXE_OVERRIDE=""
LIST=0
PLAN=0

while [ $# -gt 0 ]; do
  case "$1" in
    --only) ONLY="${2:-}"; shift 2 ;;
    --skip) SKIP="${2:-}"; shift 2 ;;
    --exe)  EXE_OVERRIDE="${2:-}"; shift 2 ;;
    --list) LIST=1; shift ;;
    --plan) PLAN=1; shift ;;
    -h|--help) sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "verify-all.sh: unknown argument '$1' (try --list, --plan, --only, --skip, --exe)" >&2; exit 2 ;;
  esac
done

# The steps, in the order they run, and what each one answers. The order is cheapest-and-most-diagnostic first:
# a static rule or a compile-time sweep names the file a later, slower step would only tell you is red.
STEPS=(invariants format strip misc embed jitplugin parity suites)
declare -A ANSWERS=(
  [invariants]="the static rules: 19 checkers, the five doc gates included"
  [format]="every suite's compiled bytecode passes BytecodeVerifier (the format sweep)"
  [strip]="the --no-source frontier, grouped, against its recorded baseline"
  [misc]="the maintainer's own 121 script corpus, which is not a suite set"
  [embed]="the published DLL's C ABI, driven by a real host process"
  [jitplugin]="a NativeAOT host reaching JIT code in a second CoreCLR, through the plugin table"
  [parity]="a stripped bundle BEHAVES like its source (the ledger of known divergences)"
  [suites]="the suite gate over assets/language/repl, on the AOT publish and the JIT build"
)
declare -A SCRIPTS=(
  [invariants]="check-invariants.sh"
  [format]="dump-bytecode.sh"
  [strip]="strip-survey.sh"
  [misc]="run-misc-scripts.sh"
  [embed]="embed-check.sh"
  [jitplugin]="jitplugin-check.sh"
  [parity]="bundle-parity.sh"
  [suites]="run-repl-tests.sh"
)

if [ "$LIST" = 1 ]; then
  echo "verify-all.sh runs these steps, in this order:"
  for name in "${STEPS[@]}"; do
    printf '  %-11s %-66s src/build/%s\n' "$name" "${ANSWERS[$name]}" "${SCRIPTS[$name]}"
  done
  echo
  echo "Everyone refuses an artifact older than src/, so publish or build before running. --only and --skip take"
  echo "comma separated names from the first column; --exe pins one engine binary for the five steps that take one"
  echo "(format, strip, misc, parity, suites)."
  exit 0
fi

# A missing instrument is a FAILURE, not a skip: a step this script claims to run and cannot is the exact rot the
# script exists to prevent.
for name in "${STEPS[@]}"; do
  if [ ! -f "$ROOT/src/build/${SCRIPTS[$name]}" ]; then
    echo "FAIL: step '$name' names src/build/${SCRIPTS[$name]}, which is not there" >&2
    exit 2
  fi
done

# The selection, and an unknown name is refused WITH the list, because a typo would otherwise silently run
# nothing and read as green.
SELECTED=()
for name in "${STEPS[@]}"; do SELECTED+=("$name"); done
if [ -n "$ONLY" ]; then
  SELECTED=()
  IFS=, read -r -a asked <<< "$ONLY"
  for want in "${asked[@]}"; do
    want="${want// /}"
    [ -n "$want" ] || continue
    found=0
    for name in "${STEPS[@]}"; do [ "$name" = "$want" ] && found=1; done
    if [ "$found" = 0 ]; then
      echo "verify-all.sh: '$want' is not a step. The steps are: ${STEPS[*]}" >&2
      exit 2
    fi
    SELECTED+=("$want")
  done
  [ "${#SELECTED[@]}" -gt 0 ] || { echo "verify-all.sh: --only named no step" >&2; exit 2; }
fi
if [ -n "$SKIP" ]; then
  IFS=, read -r -a dropped <<< "$SKIP"
  kept=()
  for name in "${SELECTED[@]}"; do
    keep=1
    for want in "${dropped[@]}"; do
      want="${want// /}"
      [ -n "$want" ] || continue
      [ "$name" = "$want" ] && keep=0
      # An unknown --skip name is NOT refused: skipping is the forgiving direction, and refusing it would make a
      # script that stops naming a step fail for having moved on.
    done
    [ "$keep" = 1 ] && kept+=("$name")
  done
  SELECTED=("${kept[@]+"${kept[@]}"}")
fi
[ "${#SELECTED[@]}" -gt 0 ] || { echo "verify-all.sh: every step was skipped, so there is nothing to verify" >&2; exit 2; }

LOG_DIR="$(mktemp -d)"
trap 'rm -rf "$LOG_DIR"' EXIT

if [ -n "$EXE_OVERRIDE" ]; then
  case "$EXE_OVERRIDE" in /*|[A-Za-z]:*) ;; *) EXE_OVERRIDE="$ROOT/$EXE_OVERRIDE" ;; esac
  [ -x "$EXE_OVERRIDE" ] || { echo "verify-all.sh: --exe '$EXE_OVERRIDE' is not an executable" >&2; exit 2; }
fi

# ── the step command lines, one function each: the options differ enough that a parameter matrix would hide which
# step got which flag, and getting one wrong is how the survey tools have had their EXE overwritten by a report.
step_cmd_invariants() { CMD=(bash "$ROOT/src/build/check-invariants.sh"); }
step_cmd_format() {
  # [out-file] BEFORE [exe]: the instruments take `[--record] [out-file] [exe]`, so an exe in the first slot
  # writes the report over the binary (which has ruined a Debug build twice).
  CMD=(bash "$ROOT/src/build/dump-bytecode.sh" "$LOG_DIR/dump-bytecode.txt")
  [ -n "$EXE_OVERRIDE" ] && CMD+=("$EXE_OVERRIDE")
}
step_cmd_strip() {
  CMD=(bash "$ROOT/src/build/strip-survey.sh" "$LOG_DIR/strip-survey.txt")
  [ -n "$EXE_OVERRIDE" ] && CMD+=("$EXE_OVERRIDE")
}
step_cmd_misc() { CMD=(bash "$ROOT/src/build/run-misc-scripts.sh"); [ -n "$EXE_OVERRIDE" ] && CMD+=("$EXE_OVERRIDE"); }
step_cmd_embed() {
  # Its arguments are (engine-dll, engine-exe), two artifacts, so --exe is NOT forwarded: the DLL is the one this
  # step is about, and the exe it wants is the child that runs a script, which it resolves itself.
  CMD=(bash "$ROOT/src/build/embed-check.sh")
}
step_cmd_jitplugin() { CMD=(bash "$ROOT/src/build/jitplugin-check.sh"); }
step_cmd_parity() {
  CMD=(bash "$ROOT/src/build/bundle-parity.sh" "$LOG_DIR/bundle-parity.txt")
  [ -n "$EXE_OVERRIDE" ] && CMD+=("$EXE_OVERRIDE")
}
step_cmd_suites() {
  CMD=(bash "$ROOT/src/build/run-repl-tests.sh" --build both)
  if [ -n "$EXE_OVERRIDE" ]; then CMD=(bash "$ROOT/src/build/run-repl-tests.sh" --exe "$EXE_OVERRIDE"); fi
}

summary_line() { # $1 = a log file: its last non-empty line, which is the instrument's own verdict
  local line
  line="$(grep -v '^[[:space:]]*$' "$1" | tail -n 1)"
  printf '%s' "${line:0:180}"
}

RESULTS=()
echo "verify-all.sh: ${#SELECTED[@]} step(s), tree '$ROOT'"
[ -n "$EXE_OVERRIDE" ] && echo "  exe pinned to '${EXE_OVERRIDE#$ROOT/}'"
echo

for name in "${SELECTED[@]}"; do
  CMD=()
  "step_cmd_$name"
  if [ "$PLAN" = 1 ]; then
    printf '  %-11s %s\n' "$name" "${CMD[*]}"
    continue
  fi
  echo "── $name: ${ANSWERS[$name]}"
  t0=$SECONDS
  "${CMD[@]}" >"$LOG_DIR/$name.log" 2>&1
  rc=$?
  secs=$((SECONDS - t0))
  RESULTS+=("$name|$rc|$secs")
  if [ "$rc" -eq 0 ]; then
    echo "ok: $name, ${secs}s: $(summary_line "$LOG_DIR/$name.log")"
  else
    echo "FAIL: $name exited $rc after ${secs}s"
    echo "  log: $LOG_DIR/$name.log (kept until this run ends)"
    tail -n 25 "$LOG_DIR/$name.log" 2>/dev/null | sed 's/^/    /'
  fi
  echo
done

[ "$PLAN" = 1 ] && exit 0

green=0
red=0
echo "── verdict"
for row in "${RESULTS[@]}"; do
  IFS='|' read -r name rc secs <<< "$row"
  if [ "$rc" -eq 0 ]; then
    printf '  ok    %-11s %5ss\n' "$name" "$secs"
    green=$((green + 1))
  else
    printf '  FAIL  %-11s %5ss  exit %s\n' "$name" "$secs" "$rc"
    red=$((red + 1))
  fi
done
echo "── ${#RESULTS[@]} step(s): $green green, $red red"
if [ "$red" -gt 0 ]; then
  echo "Fix the red step(s) above and re-run with --only <name>; every step is independent." >&2
  exit 1
fi
echo "every instrument agrees with the tree it describes"
exit 0
