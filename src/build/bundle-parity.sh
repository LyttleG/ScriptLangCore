#!/usr/bin/env bash
# bundle-parity.sh, the missing question about `--no-source`: does the BUNDLE behave like the SCRIPT?
#
# WHY IT EXISTS. Every corpus tool in this repository answers a DIFFERENT question. `run-repl-tests.sh` runs
# the .slc (never its bundle), `src/build/dump-bytecode.sh` proves a body VERIFIES, `src/build/strip-survey.sh` proves
# a file strips or refuses, and `src/build/run-misc-scripts.sh` runs the maintainer's own corpus. Nothing compared
# a stripped bundle's OUTPUT with its source's, so a suite whose bundle answers differently, or aborts, while
# its source is correct could sit in the tree and pass the gate forever: the gate runs the source. That is not
# hypothetical. Three suites were found this way by hand (crash_properties_test, where a bundle allows a
# private-setter and an `init` write the language forbids, plus crash_static_edge_cases and
# cross_module_test), and the abandoned "carried top-level statement" round measured 20 newly stripping
# suites of which 11 diverged.
#
# WHAT IT DOES, per suite under SUITE_DIR:
#   1. `--compile <suite> <bundle> --no-source`, and it requires the report to say `source stripped`. A
#      compile that SUCCEEDED by carrying the source (the route a file the strip cannot do takes) is NOT a
#      stripped bundle, and comparing it would prove nothing. That one check is what keeps this honest.
#   2. it runs the source and then the bundle, normalises both, and diffs them.
#   3. it labels each difference: DIFF when the bundle's VERDICT differs (a failed check, an abort, an error
#      line, a different exit code), which is a behaviour divergence and the class every fix rounds on, and
#      TEXT when only the free-form output differs (a diagnostic dump, a VM trace, a counter line), which is
#      still listed and still baseline-able, because a wrong PRINTED value is a divergence too.
#   4. it compares the set of differing suites against the recorded baseline, so a NEW divergence is an exit 1
#      rather than a number in a log, and one that got FIXED is reported as cleared.
#
# THE BUNDLE IS WRITTEN BESIDE ITS SUITE, not in a temp folder, and that is deliberate: a run's DIRECTORY
# comes from the file it runs, so a bundle in TEMP would answer `Script.Directory()` with TEMP and resolve a
# `./name` relative to TEMP, which is a difference of PLACE rather than one of behaviour. Beside the suite,
# both runs resolve the same way. The bundle is deleted as soon as it has run, and an EXIT trap sweeps
# `_parity_*.slcbc` (a name `*.slcbc` already ignores) so a crash cannot leave the tree dirty.
#
# THE NORMALISER DROPS WHAT CANNOT BE A BEHAVIOUR, AND NOTHING ELSE: timestamps, the two entry banners, the
# Debug build's `[Lnnnnn]` leaders, the bundle's own file name, DURATIONS (`44 ms`, `1.5 ms`, `ms=29`), a
# THROUGHPUT RATE (`333333 yields/s`) and a PORT a suite DERIVED from its own process id (`port: 9184`), plus the
# two VmStats fields that depend on warm-up rather than on behaviour (`cacheHits/misses` and `gfast`, which
# count slot-cache traffic; no suite under SUITE_DIR names either, verified by grep, and the ROUTING counters
# ` VM:`/`Tree:`/`Total:`/`toplevel:` stay visible precisely because suites do assert those).
# A duration is never a behaviour: this repository's own rule is that a suite must not assert one, and a
# benchmark prints a different number on every run, so comparing them would drown the signal in noise. A rate and
# a derived port are the same family, and both were measured as CHURN: without them `coroutine_bench` (a yields/s
# figure) and `route_duplicate_log_test` (`port: N`, derived from the pid so two runs cannot collide) flipped
# between identical and differing on consecutive runs of the SAME artifact, which makes the gate report a
# regression that is nothing of the kind.
# Everything else is compared BYTE FOR BYTE, handles, addresses, byte counts and counters included, because
# normalising those would also hide a real difference in them. A suite whose output is genuinely variable for
# another reason belongs in the baseline WITH A REASON, which is the strict direction.
#
# ONE ASYMMETRY IS INHERENT AND WORTH KNOWING: the source runs FIRST and the bundle second, so a suite that
# writes an artifact beside itself starts its bundle run with that artifact already there. A suite that cares
# shows up as a divergence here and gets a baseline entry naming the reason.
#
# Usage: bash src/build/bundle-parity.sh [--record] [--allow-stale] [--tree] [--filter NAME] [out-file] [exe]
#   --record        write the CURRENT differing set as the baseline (src/build/bundle-parity-known.txt)
#   --allow-stale   accept a binary older than src/ (otherwise this REFUSES, exactly as the gate does)
#   --tree          ALSO run the source with SLC_TOPLEVEL_VM=0 and compare its TREE leg against the default
#                   (VM) leg, so a difference between the two ENGINES is reported as TREE instead of being
#                   hidden by the source path's per-statement fallback
#   --bail          run the bundle leg with SLC_BAIL_TRACE=1 and append the FIRST bail site to a difference's
#                   detail, which is the bail-site inventory: a bundle refuses exactly where the VM bailed, so
#                   the sites the corpus hits ARE the work list
#   --filter NAME   only suites whose name contains NAME (refused with --record, a filter would truncate it)
#   out-file        optional report artifact (the summary always goes to stdout)
#   exe             optional binary (default: the newest of the AOT publish and the Debug build)
#
# Exit codes: 0 = no change against the baseline; 1 = a suite newly diverges, or one that used to diverge no
#             longer does, or a suite did not run; 2 = no exe, unwritable report, stale binary, or a
#             --record that would be truncated by --filter.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# THE ONE RULE for "does this artifact describe this tree" (see that file's header: one source set, one wording).
. "$ROOT/src/build/_artifacts.sh"

RECORD=0
ALLOW_STALE=0
TREELEG=0
BAILTRACE=0
FILTER=""
ARGS=()
want_filter=0
for a in "$@"; do
  case "$a" in
    --record) RECORD=1 ;;
    --allow-stale) ALLOW_STALE=1 ;;
    --tree) TREELEG=1 ;;
    --bail) BAILTRACE=1 ;;
    --filter) want_filter=1 ;;
    -h|--help) sed -n '2,58p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) if [ "$want_filter" -eq 1 ]; then FILTER="$a"; want_filter=0; else ARGS+=("$a"); fi ;;
  esac
done
OUT="${ARGS[0]:-}"
EXE_IN="${ARGS[1]:-}"

newest_exe() { ls -1dt "$@" 2>/dev/null | head -1; }
if [ -n "$EXE_IN" ]; then
  # An explicit exe is the caller asserting the target, so it is never second-guessed (the gate's rule).
  case "$EXE_IN" in /*|[A-Za-z]:*) EXE="$EXE_IN" ;; *) EXE="$ROOT/$EXE_IN" ;; esac
else
  # The framework directory is a GLOB, never a spelled net11.0: a TFM bump once left ten tools pointing at a
  # directory that no longer existed (AGENTS.md).
  #
  # `publish-aot/` IS CONSIDERED, and it has to be: that is where this repository's own publish commands WRITE the
  # shipping artifact (`dotnet publish ... -o publish-aot`), so a default that globbed only `bin/Release/**` measured
  # an older folder and refused every run as stale (measured: it named `bin/Release/net11.0/win-x64/publish/
  # ScriptLangCore.exe` while `publish-aot/ScriptLangCore.exe` was minutes old). The three are compared by TIME, so a
  # fresh publish in any of them wins.
  EXE="$(cd "$ROOT" && newest_exe bin/Release/net*/win-x64/publish/ScriptLangCore.exe publish-aot/ScriptLangCore.exe)"
  [ -n "$EXE" ] || EXE="$(cd "$ROOT" && newest_exe bin/Debug/net*/ScriptLangCore.exe)"
  [ -n "$EXE" ] || EXE="$ROOT/none"
fi
[ -x "$EXE" ] || {
  echo "no executable at $EXE" >&2
  echo "  build one (dotnet build -c Debug -v q --nologo) or pass one:" >&2
  echo "    bash src/build/bundle-parity.sh '' bin/Debug/net11.0/ScriptLangCore.exe" >&2
  exit 2
}

cd "$ROOT" || exit 2

# The engine's deployed layout (assets/, tools/, sqlite3.dll) lives NEXT TO the executable, which is why the
# suites run from there; a bare publish folder without it falls back to the repo root. Absolute suite paths
# are used either way.
EXE_DIR="$(cd "$(dirname "$EXE")" && pwd)"
EXE_ABS="$EXE_DIR/$(basename "$EXE")"
if [ -d "$EXE_DIR/assets" ]; then WORKDIR="$EXE_DIR"; else WORKDIR="$ROOT"; fi

# A binary older than the newest source file reports behaviour the tree no longer has, so a divergence it
# finds may already be fixed and a clean run may be a lie. An explicit exe is the caller's assertion.
# The RULE (which files count as the engine's sources, and the wording) lives in src/build/_artifacts.sh, shared with the
# gate, ab-check.sh and embed-check.sh. It prunes `src/embed` as well as `obj`/`bin`: the launcher and the C ABI host
# are OTHER PROGRAMS, and their generated intermediates once made this check refuse every run after a launcher build
# ("ScriptLangCore.exe is OLDER than src/embed/launcher/obj/.../EmbedLauncher.AssemblyInfo.cs"), which reads as a stale
# engine and is not one.
if [ "$ALLOW_STALE" -eq 0 ] && [ -z "$EXE_IN" ]; then
  if slc_refuse_stale "$EXE" "the binary" "$SLC_REMEDY_EXE" \
       "or pass the exe, or --allow-stale" "$ROOT"; then
    exit 2
  fi
fi

if [ "$RECORD" -eq 1 ] && [ -n "$FILTER" ]; then
  echo "REFUSING to --record a filtered run: it would truncate the baseline to the suites matching '$FILTER'." >&2
  exit 2
fi

if [ -n "$OUT" ]; then
  case "$OUT" in /*|[A-Za-z]:*) ;; *) OUT="$ROOT/$OUT" ;; esac
  if ! : > "$OUT" 2>/dev/null; then
    echo "cannot write the report to '$OUT', does that directory exist?" >&2
    exit 2
  fi
fi

BASELINE="$ROOT/src/build/bundle-parity-known.txt"
# WHERE THE SUITES ARE is stated once, in _suites.sh (the gate is a tree; registries key bare names).
. "$ROOT/src/build/_suites.sh"
TMP="$(mktemp -d)"
ERR="$(mktemp)"
# ONE RUN AT A TIME, and the reason is measured rather than tidy: a suite writes its artifacts with a BARE path, which
# the engine resolves against the PROCESS directory, i.e. the deployed folder this tool runs from. Two runs (or a run
# beside the gate) therefore share `_testlib.slcbc`, `*.db`, `*.csv` and `*.log`, so each would compare its own source
# leg against the other's artifact and report a difference that belongs to neither. A per-suite working copy would
# isolate them and is NOT taken: the process directory is part of what a suite OBSERVES (a bare write lands there, a
# child spawned with no directory runs there), so moving it would rewrite the recorded baseline wholesale.
LOCK="${TMPDIR:-/tmp}/slc-bundle-parity.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "FAIL: another bundle-parity run is already going (lock: $LOCK, pid $(cat "$LOCK/pid" 2>/dev/null || echo '?'))" >&2
  echo "      A sweep must run against a FROZEN exe with no build and no second run in flight." >&2
  exit 2
fi
echo $$ > "$LOCK/pid"
trap 'rm -rf "$TMP" "$LOCK"; rm -f "$ERR"; find "$ROOT/$SUITE_ROOT" -name "_parity_*.slcbc" -delete' EXIT

# The suite's own budget when src/build/repl-test-env.txt gives it one (a slow suite is slow here too), else a
# default with room for an AOT process start.
DEFAULT_TIMEOUT="${PARITY_TIMEOUT:-30}"
budget_for() {
  local b=""
  if [ -f "$ROOT/src/build/repl-test-env.txt" ]; then
    b="$(grep -a -m1 "^$1[[:space:]]" "$ROOT/src/build/repl-test-env.txt" 2>/dev/null \
         | grep -a -o 'TIMEOUT=[0-9]*' | head -1)"
    b="${b#TIMEOUT=}"
  fi
  [ -n "$b" ] || b="$DEFAULT_TIMEOUT"
  printf '%s' "$b"
}

# A baseline entry is `name<TAB>observed difference[<TAB>reason]`: the NAME is the key a regression is detected
# on, the text is what a reader needs, and the REASON is what keeps the entry honest. A behaviour divergence
# (a `DIFF` detail) MUST carry one, which the verdict enforces; a text-only one describes itself (its detail
# names the token that differs), so its reason is optional. A leading `#` line is a comment.
#
# THE MISS MUST BE A FAILURE, and it was not: both helpers piped grep into cut, and a pipeline reports the LAST
# command's status, so with no matching entry grep exited 1 and `cut` exited 0. Every caller asks
# `if baseline_detail ...`, so EVERY identical suite was reported as `cleared` (a baseline entry it never had),
# which is how a filtered run came to print a hundred suite names as "cleared". The line is captured first now,
# so an absent entry, and an entry whose field is empty, both answer non-zero.
baseline_field() {
  [ -f "$BASELINE" ] || return 1
  local line
  line="$(grep -a -m1 "^$1$(printf '\t')" "$BASELINE" 2>/dev/null)" || return 1
  [ -n "$line" ] || return 1
  printf '%s\n' "$line" | cut -f"$2"
}
baseline_detail() { baseline_field "$1" 2; }
baseline_note() { baseline_field "$1" 3; }

# The two entry paths announce themselves differently (`Running: x.slc` against `Loaded N function(s) from
# x.slcbc`) and a Debug build prefixes every printed line with its source position, so both are normalised
# away. Nothing else is: see the header on why handles and durations are NOT.
# A RUN ANNOUNCES ITSELF DIFFERENTLY ON THE TWO LEGS, and the SOURCE'S banner no longer exists (the engine stopped
# printing `[ScriptLangCore x] Running:`), while a bundle still prints `Loaded N function(s) from x.slcbc`. Both lines are
# DROPPED, not replaced by a marker: measured, replacing them left `source:[ ] bundle:[ @entry@ ]`, i.e. EVERY pair was
# reported as differing and the ledger said `identical 0` for 528 suites, which is an instrument artefact rather than
# 528 regressions.
norm() { # $1 = the bundle's file name, $2 = the source's (same place, different banner)
  sed -e '/^\[bail\]/d' \
      -e "s|$1|$2|g" \
      -e 's/^\[L[0-9][0-9]*\] //' \
      -e 's/\[[0-9][0-9]:[0-9][0-9]:[0-9][0-9]\(\.[0-9][0-9]*\)\?\] //g' \
      -e 's/^\[ScriptLangCore [^]]*\] //' \
      -e '/^Running: /d' \
      -e '/^Loaded [0-9][0-9]* function(s) from /d' \
      -e 's/\bms=[0-9][0-9]*/ms=@/g' \
      -e 's/[0-9][0-9]*\(\.[0-9][0-9]*\)\? *ms\b/@ms@/g' \
      -e 's/[0-9][0-9]*\(\.[0-9][0-9]*\)\? *ticks\b/@ticks@/g' \
      -e 's/[0-9][0-9]*\(\.[0-9][0-9]*\)\? *\(yields\|iterations\|ops\|calls\|items\|rounds\)\/s\b/@rate@\/s/g' \
      -e 's/[0-9][0-9]*\(\.[0-9][0-9]*\)\? *x faster/@ratio@x faster/g' \
      -e 's/port: [0-9][0-9]*/port: @port@/g' \
      -e 's|localhost:[0-9][0-9]*|localhost:@port@|g' \
      -e 's/cacheHits: [0-9][0-9]* misses: [0-9][0-9]*/cacheHits: @ misses: @/g' \
      -e 's/gfast: [0-9][0-9]*\/[0-9][0-9]*/gfast: @\/@/g'
}

# The VERDICT-level signal of one run: its exit code plus whatever it says about pass/fail. Comparing THIS is
# what separates "the bundle behaves differently" (a failed check, an abort, an error line) from "the bundle
# PRINTS differently" (a diagnostic dump, a VM trace, a counter line), and the two are labelled apart below so
# a reader, and the baseline, can tell them apart instead of hunting a behaviour difference that is not there.
sig() { # $1 = exit code, $2 = the raw output capture
  printf 'exit=%s\n' "$1"
  # The Debug build prefixes EVERY printed line with its source position, and a tally line or an `[ERROR]`
  # line therefore carries a `[Lnnnnn] ` the normaliser would have dropped. Dropping it HERE too is what
  # stops a Debug-build leg from reporting a difference that is only that prefix (measured on crash_regex:
  # the TREE leg was reported differing while both sides read `67 passed, 0 failed`).
  grep -a -E '\[ERROR\]|TESTS? FAILED|[0-9]+ passed, [0-9]+ failed' "$2" \
    | sed -e 's/^\[L[0-9][0-9]*\] *//' -e 's/^\[[0-9:.]*\] *//'
}

n=0; identical=0; differing=0; known=0; cleared=0; refused=0; carried=0; notrun=0; textonly=0; unreasoned=0; flaked=0
: > "$TMP/all.txt"
: > "$TMP/new.txt"
: > "$TMP/cleared.txt"
for suite in $(suite_files); do
  name="$(basename "$suite")"
  case "$name" in *"$FILTER"*) ;; *) continue ;; esac
  n=$((n + 1))
  # The bundle is written BESIDE ITS SUITE, because a bundle's `./` resolves in the bundle's own directory:
  # a bundle parked at the gate root would make a suite that reads a sibling file (bench_comprehensive reads
  # bench_coroutine.slc) differ for a reason that is a difference of PLACE, not of behaviour.
  bundle="$(dirname "$suite")/_parity_${name%.slc}.slcbc"
  rm -f "$ROOT/$bundle"

  report="$("$EXE_ABS" --compile "$ROOT/$suite" "$ROOT/$bundle" --no-source 2>&1)"
  rc=$?
  if [ "$rc" -ne 0 ]; then
    rm -f "$ROOT/$bundle"
    if [ "$rc" -eq 1 ] || [ "$rc" -eq 3 ]; then
      refused=$((refused + 1))
    else
      notrun=$((notrun + 1))
      printf '  notrun   %-42s (exit %s, did not compile at all)\n' "$name" "$rc"
    fi
    continue
  fi
  # A compile that succeeded by CARRYING the source is not a stripped bundle. Compared anyway, it would
  # always match, which is the one way this tool could lie, so it is skipped by name.
  case "$report" in
    *"source stripped"*) ;;
    *) carried=$((carried + 1)); rm -f "$ROOT/$bundle"; continue ;;
  esac

  budget="$(budget_for "$name")"
  ( cd "$WORKDIR" && timeout -k 5 "$budget" "$EXE_ABS" "$ROOT/$suite" ) > "$TMP/f.out" 2>&1
  frc=$?
  if [ "$BAILTRACE" -eq 1 ]; then
    ( cd "$WORKDIR" && env SLC_BAIL_TRACE=1 timeout -k 5 "$budget" "$EXE_ABS" "$ROOT/$bundle" ) > "$TMP/b.out" 2>&1
  else
    ( cd "$WORKDIR" && timeout -k 5 "$budget" "$EXE_ABS" "$ROOT/$bundle" ) > "$TMP/b.out" 2>&1
  fi
  brc=$?
  rm -f "$ROOT/$bundle"

  # THE TREE LEG (--tree): the SOURCE again with SLC_TOPLEVEL_VM=0, so its top level runs on the TREE WALKER
  # while the default leg runs it on the VM. Nothing else in the tree compares those two engines, and a source
  # run HIDES a VM gap by re-running a bailed statement on the tree, so the two can disagree without either one
  # failing. That is the axis the round-1 A/B found ~26 divergences on (a unary `~` on a bool, an unsigned
  # comparison read as signed, a dictionary literal built in reverse, an enum under a bitwise operator), and it
  # is a property of the ENGINE rather than of a bundle.
  trc=0
  if [ "$TREELEG" -eq 1 ]; then
    ( cd "$WORKDIR" && env SLC_TOPLEVEL_VM=0 timeout -k 5 "$budget" "$EXE_ABS" "$ROOT/$suite" ) > "$TMP/t.out" 2>&1
    trc=$?
  fi

  bn_bundle="$(basename "$bundle")"
  f_norm="$(norm "$bn_bundle" "$name" < "$TMP/f.out")"
  b_norm="$(norm "$bn_bundle" "$name" < "$TMP/b.out")"
  f_sig="$(sig "$frc" "$TMP/f.out")"
  b_sig="$(sig "$brc" "$TMP/b.out")"
  t_norm=""; t_sig=""
  if [ "$TREELEG" -eq 1 ]; then
    t_norm="$(norm "$bn_bundle" "$name" < "$TMP/t.out")"
    t_sig="$(sig "$trc" "$TMP/t.out")"
  fi

  same="yes"
  [ "$f_sig" = "$b_sig" ] || same="no"
  [ "$f_norm" = "$b_norm" ] || same="no"
  if [ "$TREELEG" -eq 1 ]; then
    [ "$t_sig" = "$f_sig" ] || same="no"
    [ "$t_norm" = "$f_norm" ] || same="no"
  fi
  if [ "$same" = "no" ]; then
    # A DIVERGENCE THAT DISAPPEARS ON A SECOND RUN IS A FLAKE, NOT A BEHAVIOUR, and nothing else in the ledger can tell
    # the two apart: measured twice on the SAME artifact, `com_lifetime_test`'s BUNDLE exited 150 once and passed on the
    # next run, and `crash_udf_leak`'s SOURCE exited 1 once and passed on the next, because the first activates COM and
    # the second opens a SQLite file in the shared working directory. So a differing suite is run ONCE MORE (both legs,
    # and the bundle recompiled, since the first pass deleted it) and a difference that does not reproduce is counted as
    # FLAKED: printed, never a regression, never recorded. A real divergence reproduces and falls through unchanged.
    #
    # THE TEST IS "DO THE SECOND PASS'S TWO LEGS AGREE", and that condition was written as something else first, which is
    # why this note exists: it ALSO required the second pass to REPEAT the first pass's exit codes, so a difference that
    # VANISHED failed it, which is precisely the flake it was meant to catch. Measured on `rich_edit_control_test` (the
    # recorded UI paint/focus race): its bundle exited 2 once and 0 on the next run, so the branch never fired, the run
    # counted 1 unreasoned behaviour divergence and the gate went red over a suite that agrees with itself.
    "$EXE_ABS" --compile "$ROOT/$suite" "$ROOT/$bundle" --no-source >/dev/null 2>&1
    ( cd "$WORKDIR" && timeout -k 5 "$budget" "$EXE_ABS" "$ROOT/$suite" ) > "$TMP/f2.out" 2>&1
    f2rc=$?
    ( cd "$WORKDIR" && timeout -k 5 "$budget" "$EXE_ABS" "$ROOT/$bundle" ) > "$TMP/b2.out" 2>&1
    b2rc=$?
    rm -f "$ROOT/$bundle"
    if [ "$(norm "$bn_bundle" "$name" < "$TMP/f2.out")" = "$(norm "$bn_bundle" "$name" < "$TMP/b2.out")" ] \
       && [ "$(sig "$f2rc" "$TMP/f2.out")" = "$(sig "$b2rc" "$TMP/b2.out")" ]; then
      flaked=$((flaked + 1))
      printf '  flaked   %-42s (did not reproduce: first pass source exit %s, bundle exit %s; second pass %s and %s)\n' \
        "$name" "$frc" "$brc" "$f2rc" "$b2rc"
      continue
    fi
  fi
  if [ "$same" = "yes" ]; then
    identical=$((identical + 1))
    if baseline_detail "$name" >/dev/null 2>&1; then
      cleared=$((cleared + 1))
      printf '%s\n' "$name" >> "$TMP/cleared.txt"
      printf '  cleared  %s\n' "$name"
    fi
    continue
  fi

  # WHICH leg(s) differ, named so a reader is not sent hunting the wrong one. DIFF is the BUNDLE's verdict
  # against the source's (the class the tool exists for), TREE is the source run's TREE leg against its own VM
  # leg, and TEXT is free-form output only: listed and baseline-able, because a wrong PRINTED value is a
  # divergence too, but labelled apart so a benchmark counter does not read as behaviour.
  differing=$((differing + 1))
  kind=""; onlydiff=""; other="bundle"
  if [ "$f_sig" != "$b_sig" ]; then
    # BEHAVIOUR: the same suite run from its bundle failed a check, aborted, or reported an error.
    kind="DIFF"
    onlydiff="$(diff <(printf '%s\n' "$f_sig") <(printf '%s\n' "$b_sig"))"
  fi
  if [ "$TREELEG" -eq 1 ] && [ "$t_sig" != "$f_sig" ]; then
    kind="${kind:+$kind+}TREE"
    [ -n "$onlydiff" ] || onlydiff="$(diff <(printf '%s\n' "$f_sig") <(printf '%s\n' "$t_sig"))"
    [ "$kind" = "TREE" ] && other="tree (SLC_TOPLEVEL_VM=0)"
  fi
  if [ -z "$kind" ]; then
    kind="TEXT"
    onlydiff="$(diff <(printf '%s\n' "$f_norm") <(printf '%s\n' "$b_norm"))"
  fi
  case "$kind" in *DIFF*|*TREE*) ;; *) textonly=$((textonly + 1)) ;; esac
  # BOTH sides of the first difference, because one side alone cannot be diagnosed, and because the pair is
  # what the baseline records. A run that did not finish is its own verdict: no line comparison describes an
  # abort.
  dl_file="$(printf '%s\n' "$onlydiff" | grep -a -m1 -E '^<' | cut -c3-)"
  dl_bundle="$(printf '%s\n' "$onlydiff" | grep -a -m1 -E '^>' | cut -c3-)"
  detail="${kind}: source:[ ${dl_file} ] ${other}:[ ${dl_bundle} ]"
  [ -n "$dl_file$dl_bundle" ] || detail="${kind}: (no line differs)"
  [ "$frc" -eq 124 ] || [ "$frc" -eq 137 ] && detail="${kind}: the source itself timed out at ${budget}s"
  [ "$brc" -eq 124 ] || [ "$brc" -eq 137 ] && detail="${kind}: the BUNDLE timed out at ${budget}s"
  [ "$TREELEG" -eq 1 ] && { [ "$trc" -eq 124 ] || [ "$trc" -eq 137 ]; } && detail="${kind}: the TREE leg timed out at ${budget}s"
  [ "$frc" -ne 0 ] && [ "$frc" -ne 124 ] && [ "$frc" -ne 137 ] && detail="${kind}: the source exited $frc"
  [ "$brc" -ne 0 ] && [ "$brc" -ne 124 ] && [ "$brc" -ne 137 ] && detail="${kind}: the BUNDLE exited $brc"
  [ "$TREELEG" -eq 1 ] && [ "$trc" -ne 0 ] && [ "$trc" -ne 124 ] && [ "$trc" -ne 137 ] && detail="${kind}: the TREE leg exited $trc"
  detail="$(printf '%s' "$detail" | sed 's/[[:space:]]\+/ /g' | cut -c1-400)"
  # The BAIL SITE, appended AFTER the cut so it always survives: a bundle refuses exactly where the VM bailed,
  # so these are the work list for closing the VM's gaps. The trace lines themselves are dropped from the
  # comparison (see norm), so asking for the trace cannot change a verdict.
  if [ "$BAILTRACE" -eq 1 ]; then
    bailsite="$(grep -a -m1 -o '\[bail\] Vm\.cs:[0-9]*' "$TMP/b.out")"
    [ -n "$bailsite" ] && detail="$detail  |$bailsite|"
  fi

  printf '%s\t%s\n' "$name" "$detail" >> "$TMP/all.txt"
  if prior="$(baseline_detail "$name")"; then
    known=$((known + 1))
    printf '  known    %-42s %s\n' "$name" "$detail"
    [ "$prior" = "$detail" ] || printf '  ...its baseline text CHANGED:\n    was: %s\n' "$prior"
    # A behaviour divergence must SAY WHY it is acceptable; the verdict refuses one that does not.
    case "$kind" in
      *DIFF*)
        # A TEXT row carries no reason (it does not need one), so a suite whose difference CHANGES CLASS to behaviour has
        # nothing to read. Both cases are refused, and both are NAMED in the verdict below, because "1 divergence carries
        # NO REASON" without a name is what sent a reader hunting through a 335-row ledger.
        if [ -n "$(baseline_note "$name")" ]; then :; else
          unreasoned=$((unreasoned + 1))
          if [ -n "$(baseline_detail "$name")" ]; then
            printf '  %s\tchanged class: the baseline records a TEXT difference, this run is BEHAVIOUR\n' "$name" \
              >> "$TMP/unreasoned.txt"
          else
            printf '  %s\tnew behaviour divergence, no entry in the baseline\n' "$name" >> "$TMP/unreasoned.txt"
          fi
        fi
        ;;
    esac
  else
    printf '%s\n' "$name" >> "$TMP/new.txt"
    printf '  %-8s %-42s %s\n' "$kind" "$name" "$detail"
    case "$kind" in
      *DIFF*)
        unreasoned=$((unreasoned + 1))
        printf '  %s\tnew behaviour divergence, no entry in the baseline\n' "$name" >> "$TMP/unreasoned.txt"
        ;;
    esac
  fi
done

RECORDED=0
if [ "$RECORD" -eq 1 ]; then
  # THE REASONS ARE READ BEFORE THE FILE IS REOPENED, and that order is the whole point: the block below writes
  # `> "$BASELINE"`, and a redirection TRUNCATES its target before the block runs, so reading the baseline from
  # inside it sees an EMPTY file. Measured: the first record after this feature landed kept ZERO reasons for
  # exactly that reason (42 entries came back unreasoned), which is the same class of mistake as the `NR == FNR`
  # one below, in the same function.
  kept="$TMP/notes.kept"
  : > "$kept"
  if [ -f "$BASELINE" ]; then
    awk -F"$(printf '\t')" '$3 != "" { print $1 "\t" $3 }' "$BASELINE" > "$kept"
  fi
  {
    echo "# Suites whose stripped bundle does NOT behave like its source, with the observed difference."
    echo "# Written by 'bash src/build/bundle-parity.sh --record'. An entry is a KNOWN, justified divergence:"
    echo "# it keeps the run green while the reason holds, and it is reported when it stops holding."
    echo "# A divergence that is a DEFECT belongs in the engine, not here; a genuinely variable output"
    echo "# (a wall-clock, a byte count) is what this file is for."
    echo "#"
    echo "# THE THIRD FIELD IS THE REASON, and a DIFF (behaviour) entry without one FAILS the gate:"
    echo "#   by construction: <what the suite needs that a bundle cannot be>"
    echo "#   defect: <the engine gap, and what would close it>"
    echo "# A TEXT entry describes itself (its detail names the differing token), so its reason is optional."
    echo "# A re-record KEEPS the reason an entry already carries, matched by name, so classifying is done once."
    awk -F"$(printf '\t')" -v OFS="$(printf '\t')" '
      # The marker is the FILE NAME, not `NR == FNR`: that idiom cannot tell the two files apart when the first
      # is empty, and `kept` is empty until a reason exists.
      FILENAME == ARGV[1] { note[$1] = $2; next }
      { if ($1 in note) print $1, $2, note[$1]; else print $1, $2 }' "$kept" "$TMP/all.txt" \
      | sort -t"$(printf '\t')" -k1,1
  } > "$BASELINE"
  RECORDED=1
fi

{
  echo "── exe: ${EXE#$ROOT/}"
  echo "── working directory: ${WORKDIR#$ROOT/}"
  echo "── differing suites ($differing):"
  if [ -s "$TMP/all.txt" ]; then
    while IFS="$(printf '\t')" read -r nm dt; do
      printf '   %-42s %s\n' "$nm" "$dt"
    done < "$TMP/all.txt"
  else
    echo "   (none)"
  fi
} | tee ${OUT:+"$OUT"}

if [ "$RECORD" -eq 1 ]; then
  echo "recorded the baseline: ${BASELINE#$ROOT/} ($differing suite(s) differing)"
fi

regressed=0
if [ "$RECORD" -eq 0 ]; then
  if [ -f "$BASELINE" ]; then
    # The verdict is a CHANGE, not a count: a name that was not there before is a regression, and a name that
    # vanished is a fix whose baseline entry should go. Both are keyed on the SUITE NAME, so a difference text
    # that drifts cannot hide one.
    #
    # A FILTERED run must only ask that of the suites it actually RAN: the baseline covers the whole corpus, so
    # every entry it did not compare would otherwise read as "cleared" (measured: a one-suite filter printed a
    # hundred names as cleared). `ran.txt` is the compared set, and `was.txt` is the baseline restricted to it.
    for s in $(suite_files); do
      n="$(basename "$s")"
      case "$n" in *"$FILTER"*) printf '%s\n' "$n" ;; esac
    done | sort > "$TMP/ran.txt"
    cut -f1 "$BASELINE" | grep -a -v '^#' | grep -a -v '^[[:space:]]*$' | sort > "$TMP/wasall.txt"
    comm -12 "$TMP/wasall.txt" "$TMP/ran.txt" > "$TMP/was.txt"
    cut -f1 "$TMP/all.txt" | sort > "$TMP/is.txt"
    appeared="$(comm -13 "$TMP/was.txt" "$TMP/is.txt")"
    vanished="$(comm -23 "$TMP/was.txt" "$TMP/is.txt")"
    if [ -n "$appeared" ]; then
      regressed=1
      echo "REGRESSED, these suites' bundles now answer differently from their source:" >&2
      printf '%s\n' "$appeared" | sed 's/^/  /' >&2
    fi
    if [ -n "$vanished" ]; then
      echo "cleared, these suites now match (re-record with --record to drop their entries):"
      printf '%s\n' "$vanished" | sed 's/^/  /'
    fi
  else
    echo "no baseline yet (src/build/bundle-parity-known.txt): run this once with --record to create it"
    # A first run that FINDS something must not exit 0: with nothing to compare against, the divergences are
    # the whole verdict, and reading them as a pass is how a first sweep would be mistaken for a clean one.
    if [ "$differing" -gt 0 ]; then
      regressed=1
      echo "and this run found $differing divergence(s), with no recorded set to compare them against:" >&2
      sed 's/^/  /' "$TMP/all.txt" >&2
    fi
  fi
fi

echo "parity: $n suite(s) compared: $((identical + differing)) (identical $identical, differing $differing: behaviour $((differing - textonly)), text-only $textonly; $known known, $((differing - known)) new, $unreasoned unreasoned)"
echo "        skipped: $refused refusing, $carried carrying their source; did not run: $notrun; flaked (did not reproduce on a second run): $flaked"
if [ "$RECORDED" -eq 1 ]; then
  if [ "$unreasoned" -gt 0 ]; then
    echo "$unreasoned behaviour divergence(s) still need a REASON in ${BASELINE#$ROOT/}:" >&2
    if [ -f "$TMP/unreasoned.txt" ]; then sed 's/^/  /' "$TMP/unreasoned.txt" >&2; fi
    echo "  add a third, tab-separated field: 'by construction: ...' or 'defect: ...'." >&2
  fi
  exit 0
fi
# A behaviour divergence without a reason is an unclassified difference, which is exactly what a baseline is
# not for: the gate stays red until somebody says WHY the bundle may differ from its source.
if [ "$unreasoned" -ne 0 ]; then
  echo "$unreasoned behaviour divergence(s) carry NO REASON in ${BASELINE#$ROOT/}:" >&2
  if [ -f "$TMP/unreasoned.txt" ]; then sed 's/^/  /' "$TMP/unreasoned.txt" >&2; fi
  echo "  a DIFF entry must say WHY ('by construction: ...' or 'defect: ...'), or it is decoration." >&2
  regressed=1
fi
if [ "$regressed" -ne 0 ] || [ "$notrun" -ne 0 ]; then
  exit 1
fi
exit 0
