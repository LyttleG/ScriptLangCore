#!/usr/bin/env bash
# run-repl-tests.sh, gate for the .slc suites under assets/language/repl.
#
# Why this exists: the suites were previously run by ad-hoc shell loops, which failed in three
# ways, the Debug exe disappears between commands, huge single-line outputs produced false
# positives when grepping for "failed", and there was no per-script timeout or record of the
# scripts that are already broken. This runner fixes all three:
#   * picks the NEWEST available build (a stale AOT artifact would silently report old behaviour)
#     and warns when the chosen binary is older than the newest source file,
#   * runs each suite from the EXE's directory, the deployed layout (assets/, tools/,
#     sqlite3.dll) lives next to the binary, and using the repo root as cwd made every sqlite
#     suite fail for the wrong reason,
#   * classifies by EXIT CODE first (suites end with exit(CheckFailed())) plus the authoritative
#     'N passed, M failed' summary line, with a truncated [ERROR] scan (never greps the whole
#     output for "failed"),
#   * enforces a per-script timeout,
#   * compares against src/build/repl-test-known-fail.txt and src/build/repl-test-skip.txt,
#   * skips src/build/repl-test-aot-only.txt suites on any non-AOT platform (they assert that MILLIONS of
#     nested calls survive, true on AOT, impossible on the JIT's 24-frame cap, so they run and gate on
#     AOT only; the skip is counted, never hidden),
#   * applies per-suite overrides from src/build/repl-test-env.txt: VAR=VALUE pairs exported for that one
#     suite (e.g. _vm_gen raising SLC_VM_MAXFRAMES for flat generator recursion, a global raise would
#     remove the runaway-recursion guard other suites assert) and a TIMEOUT=SECONDS knob for suites that
#     legitimately need longer than the default 20s, so they can GATE instead of being parked in the
#     known-fail registry, where their checks would never run again.
#
# Usage:
#   src/build/run-repl-tests.sh                  # everything not skipped (AOT-primary by default)
#   src/build/run-repl-tests.sh --filter match   # only scripts whose name contains "match"
#   src/build/run-repl-tests.sh --timeout 30 --verbose
#   src/build/run-repl-tests.sh --build newest   # newest-of-the-builds (the pre-AOT-first default)
#   src/build/run-repl-tests.sh --build jit      # force the Debug/JIT binary (deterministic)
#   src/build/run-repl-tests.sh --build aot      # force the NativeAOT publish binary
#   src/build/run-repl-tests.sh --build both     # gate BOTH platforms in one command, per-platform report
#   src/build/run-repl-tests.sh --exe bin/Debug/net11.0/ScriptLangCore.exe
#   src/build/run-repl-tests.sh --json           # machine-readable summary
#
# --build exists because the default is "newest of the available builds": that is right for day-to-day
# work (a stale artifact must never report old behaviour) but wrong for verifying a specific platform,
# a fresh AOT publish silently switches the gate to the AOT path, leaving JIT-only behaviour (the
# 1.5MB-apphost stack guard, the SLC_VM_MAXFRAMES override in src/build/repl-test-env.txt) uncovered.
# Gate one platform with --build, or both by running the suite twice, once per --build value.
#
# Exit code: 0 when there is no UNEXPECTED failure (known failures do not fail the run),
# 1 when a script fails unexpectedly, 2 on setup problems (no runner binary).

set -u
export PATH="/usr/bin:/bin:/usr/local/bin:$PATH"

ARGS=("$@")   # kept verbatim: --build both re-invokes this script once per platform

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# THE ONE RULE for "does this artifact describe this tree", shared with bundle-parity.sh, ab-check.sh and
# embed-check.sh (see that file's header for why it is shared rather than restated).
. "$ROOT/src/build/_artifacts.sh"
cd "$ROOT" || exit 2

# WHERE THE SUITES ARE, and how one is found by NAME, is stated once in _suites.sh: the gate's suites are a
# TREE now (repl/<area>/<suite>.slc) while every registry and baseline keys them by bare filename.
. "$ROOT/src/build/_suites.sh"
SKIP_FILE="src/build/repl-test-skip.txt"
KNOWN_FAIL_FILE="src/build/repl-test-known-fail.txt"
ENV_FILE="src/build/repl-test-env.txt"
SLOW_FILE="src/build/repl-test-slow.txt"
AOT_ONLY_FILE="src/build/repl-test-aot-only.txt"

# ── the runner binaries, resolved by GLOB rather than by a hardcoded target framework ──
# A TFM bump (net10.0 -> net11.0) used to leave every tool pointing at a directory that no longer
# existed: `dotnet clean` plus the rebuild removed bin/**/net10.0, and the gate exited 2 with nothing to
# run (measured 2026-10-08). The glob follows the project file instead, and `-t` keeps the NEWEST of a
# matched set, so a leftover output of an older framework cannot win over a fresh one.
newest_exe() { ls -1dt "$@" 2>/dev/null | head -1; }
AOT_BIN="$(newest_exe bin/Release/net*/win-x64/publish/ScriptLangCore.exe)"
JIT_BIN="$(newest_exe bin/Debug/net*/ScriptLangCore.exe)"

FILTER=""
TIMEOUT_S=20
VERBOSE=0
QUIET=0            # progress is on by default: a silent run cannot be told apart from a wedged one
PROFILE="full"     # full | correctness, correctness skips the wall-clock suites (src/build/repl-test-slow.txt)
JSON=0
EXE_OVERRIDE=""
ALLOW_STALE=0      # --allow-stale: verify a given AOT publish as-is, even if older than src
BUILD_SEL="aot-first"   # the shipping target: prefer the NativeAOT publish
while [ $# -gt 0 ]; do
  case "$1" in
    --filter)  FILTER="${2:-}"; shift 2 ;;
    --timeout) TIMEOUT_S="${2:-20}"; shift 2 ;;
    --exe)     EXE_OVERRIDE="${2:-}"; shift 2 ;;
    --allow-stale) ALLOW_STALE=1; shift ;;
    --build)   BUILD_SEL="${2:-newest}"; shift 2 ;;
    --verbose) VERBOSE=1; shift ;;
    --quiet)   QUIET=1; shift ;;
    --profile) PROFILE="${2:-full}"; shift 2 ;;
    --json)    JSON=1; shift ;;
    -h|--help) sed -n '2,38p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

# ── --build both: gate every AVAILABLE platform in one command ──
# Done by re-invoking this script once per platform (with --build stripped from the arguments), so every
# per-build rule, newest-vs-forced selection, registries, per-suite env overrides, timeout, reporting,
# stays exactly as it is for a single run. Platforms with no binary are reported and skipped; the exit
# code is the worst of the runs (2 beats 1). JSON output is single-build by design, so it is refused
# here rather than emitting two documents that look like one.
if [ "$BUILD_SEL" = "both" ]; then
  if [ -n "$EXE_OVERRIDE" ]; then
    echo "--exe and --build both are mutually exclusive: --exe pins ONE binary." >&2; exit 2
  fi
  if [ "$JSON" -eq 1 ]; then
    echo "--json is single-build: run --build both without --json, or gate one platform with --build." >&2; exit 2
  fi
  sub_args=(); i=0
  while [ $i -lt ${#ARGS[@]} ]; do
    if [ "${ARGS[$i]}" = "--build" ]; then i=$((i + 2)); continue; fi
    sub_args+=("${ARGS[$i]}"); i=$((i + 1))
  done
  ran=0; rc_all=0
  for platform in aot jit; do
    case "$platform" in
      jit) bin="$JIT_BIN" ;;
      aot) bin="$AOT_BIN" ;;
    esac
    if [ ! -x "$bin" ]; then
      # An empty $bin means the glob matched NOTHING, so naming it would print "skipped ( is missing)":
      # say which build is absent and how to make it (measured 2026-10-08 after a `dotnet clean`).
      if [ -z "$bin" ]; then
        case "$platform" in
          jit) echo "── platform jit: skipped (no bin/Debug/net*/ScriptLangCore.exe: build one with dotnet build ScriptLangCore.csproj -c Debug -v q --nologo)" >&2 ;;
          aot) echo "── platform aot: skipped (no bin/Release/net*/win-x64/publish/ScriptLangCore.exe: publish one with dotnet publish ScriptLangCore.csproj -c Release -v q --nologo)" >&2 ;;
        esac
      else
        echo "── platform $platform: skipped ($bin is missing)" >&2
      fi
      continue
    fi
    ran=$((ran + 1))
    echo
    echo "════════════════ platform: $platform ════════════════"
    bash "${BASH_SOURCE[0]}" --build "$platform" ${sub_args[@]+"${sub_args[@]}"}
    rc=$?
    if [ $rc -eq 2 ]; then rc_all=2
    elif [ $rc -ne 0 ] && [ $rc_all -ne 2 ]; then rc_all=1
    fi
  done
  if [ "$ran" -eq 0 ]; then
    echo "no runner build available: dotnet build ScriptLangCore.csproj -c Debug -v q --nologo  and/or  dotnet publish ScriptLangCore.csproj -c Release -v q --nologo" >&2
    exit 2
  fi
  echo
  echo "gated $ran platform(s): combined exit=$rc_all"
  exit $rc_all
fi

# ── runner binary: the NEWEST of the available builds ──
EXE=""
newest=0
for candidate in \
  bin/Release/net*/win-x64/publish/ScriptLangCore.exe \
  bin/Release/net*/ScriptLangCore.exe \
  bin/Debug/net*/ScriptLangCore.exe; do
  if [ -x "$candidate" ]; then
    mt=$(stat -c %Y "$candidate" 2>/dev/null || echo 0)
    if [ "$mt" -gt "$newest" ]; then newest="$mt"; EXE="$candidate"; fi
  fi
done
PLATFORM="unknown"
if [ -n "$EXE_OVERRIDE" ]; then
  if [ ! -x "$EXE_OVERRIDE" ]; then echo "runner binary not executable: $EXE_OVERRIDE" >&2; exit 2; fi
  EXE="$EXE_OVERRIDE"; PLATFORM="custom"
elif [ "$BUILD_SEL" = "jit" ] || [ "$BUILD_SEL" = "aot" ]; then
  case "$BUILD_SEL" in
    jit) EXE="$JIT_BIN" ;;
    aot) EXE="$AOT_BIN" ;;
  esac
  PLATFORM="$BUILD_SEL"
  if [ ! -x "$EXE" ]; then
    echo "--build $BUILD_SEL requested, but $EXE is missing." >&2
    case "$BUILD_SEL" in
      jit) echo "  build it: dotnet build ScriptLangCore.csproj -c Debug -v q --nologo" >&2 ;;
      aot) echo "  build it: dotnet publish ScriptLangCore.csproj -c Release -v q --nologo" >&2 ;;
    esac
    exit 2
  fi
elif [ "$BUILD_SEL" = "aot-first" ]; then
  # The goal is to SHIP on NativeAOT, so that is what the gate tests by default. Falling back to the
  # JIT build keeps the gate usable before the first publish, but says so loudly: a JIT-only pass is
  # not evidence about the shipping binary. --build newest restores the old newest-wins behaviour.
  if [ -x "$AOT_BIN" ]; then EXE="$AOT_BIN"; PLATFORM="aot"
  else
    echo "WARNING: no NativeAOT publish at $AOT_BIN, gating the JIT build instead." >&2
    echo "         The JIT build is NOT the shipping target; publish it: dotnet publish ScriptLangCore.csproj -c Release -v q --nologo" >&2
    PLATFORM="jit(fallback)"
  fi
elif [ "$BUILD_SEL" != "newest" ]; then
  echo "unknown --build value: $BUILD_SEL (expected aot-first|newest|jit|aot|both)" >&2; exit 2
fi
if [ -z "$EXE" ]; then
  echo "No runner binary found. Build one first:" >&2
  echo "  dotnet build -c Debug -v q --nologo      # or: dotnet publish ScriptLangCore.csproj -c Release   (AOT)" >&2
  exit 2
fi
# Label the gated platform for the summary, the newest-of path does not choose one explicitly.
if [ "$PLATFORM" = "unknown" ]; then
  case "$EXE" in
    *publish*) PLATFORM="aot" ;;
    *Debug*)   PLATFORM="jit" ;;
    *)         PLATFORM="custom" ;;
  esac
fi

# The engine's deployed layout (assets/, tools/, sqlite3.dll) lives NEXT TO the executable, so the
# suites must run from there, that is exactly why every sqlite suite failed when the runner used
# the repo root as its working directory. Fall back to the repo root when that layout is absent
# (e.g. a bare AOT publish folder); absolute script paths are used either way.
EXE_DIR="$(cd "$(dirname "$EXE")" && pwd)"
EXE_ABS="$EXE_DIR/$(basename "$EXE")"
if [ -d "$EXE_DIR/assets" ]; then WORKDIR="$EXE_DIR"; else WORKDIR="$ROOT"; fi

# A binary older than the newest source file cannot be trusted to reflect the tree. WHICH files count as "the sources
# that build this binary", and the wording of the refusal, live in src/build/_artifacts.sh, shared with
# bundle-parity.sh, ab-check.sh and embed-check.sh: four instruments answering that question in their own words is how
# a stale artifact got REFUSED by the gate and ACCEPTED by another tool, which is worse than either rule.
if [ -z "$EXE_OVERRIDE" ] && [ "$ALLOW_STALE" = 0 ]; then
  # Any AOT path refuses, not just the default selection: an explicit --build aot once only WARNED, the
  # warning was easy to filter away, and a long debugger chase followed a stale publish. The JIT path
  # refuses the SAME way now: a warning there was just as easy to miss, and it silently gated a stale
  # Debug binary for a whole session (every JIT "failure" reported then came from a build older than src).
  # An explicit --exe is the caller asserting the target; --allow-stale the deliberate "verify as-is".
  case "$PLATFORM" in
    aot)
      if slc_refuse_stale "$EXE" "the AOT publish" "$SLC_REMEDY_EXE" \
           "deliberately verifying this exact artifact? --allow-stale   (or gate the JIT build: --build jit)" "$ROOT"; then
        exit 2
      fi ;;
    jit|jit\(fallback\))
      if slc_refuse_stale "$EXE" "the JIT (Debug) build" "dotnet build ScriptLangCore.csproj -c Debug -v q --nologo" \
           "deliberately verifying this exact artifact? --allow-stale" "$ROOT"; then
        echo "  A stale Debug binary gates OLD behaviour, so it would report failures that no longer exist." >&2
        exit 2
      fi ;;
  esac
elif [ -n "$EXE_OVERRIDE" ] || [ "$ALLOW_STALE" != 0 ]; then
  slc_warn_stale "$EXE" "the build you asked for" "$ROOT"
fi

# ── registries: lines are `name-or-glob  # comment`. The comment is stripped AFTER the name so
#    file names containing spaces (e.g. "dbScript - Copie.slc") still match. ──
registry_match() {   # $1 = registry file, $2 = basename
  [ -f "$1" ] || return 1
  while IFS= read -r line; do
    entry="$(printf '%s' "$line" | sed 's/#.*//' | sed 's/[[:space:]]*$//')"
    [ -z "$entry" ] && continue
    case "$2" in $entry) return 0 ;; esac
  done < "$1"
  return 1
}
skip_match() { registry_match "$SKIP_FILE" "$1"; }
known_fail() { registry_match "$KNOWN_FAIL_FILE" "$1"; }

# Per-suite overrides: `name.slc VAR=VALUE … [TIMEOUT=SECONDS]` (comment stripped after the name).
# Printed as "<timeout>|<VAR=VALUE …>" so one parse serves both consumers. TIMEOUT= is a RUNNER knob
# (it becomes the `timeout` argument for that suite); every other token is exported into the engine's
# environment. Scope is always the single suite on the line: a global raise would remove the depth guard
# the runaway-recursion suites assert, and a global timeout would hide a real hang.
# Per-suite overrides are loaded ONCE into associative arrays. The previous version re-read the whole env
# file for EVERY suite with two `sed` forks per line: measured 3.12s per call, ~40 minutes of pure re-parsing
# in one --build both run, which is why the gate felt slow for no visible reason.
declare -A OVR_TIMEOUT=()
declare -A OVR_ENV=()
load_suite_overrides() {
  [ -f "$ENV_FILE" ] || return 0
  local line entry name tok t v
  while IFS= read -r line; do
    entry="${line%%#*}"                                  # strip comment, pure bash, no fork
    entry="${entry%"${entry##*[![:space:]]}"}"           # rstrip
    [ -z "$entry" ] && continue
    # shellcheck disable=SC2086 # deliberate: split into name + assignments
    set -- $entry
    name="$1"
    [ $# -lt 2 ] && continue
    shift
    t=""; v=""
    for tok in "$@"; do
      case "$tok" in
        TIMEOUT=*) t="${tok#TIMEOUT=}" ;;
        *) v="$v $tok" ;;
      esac
    done
    OVR_TIMEOUT["$name"]="$t"
    OVR_ENV["$name"]="${v# }"
  done < "$ENV_FILE"
}

TMP_OUT="$(mktemp)"
trap 'rm -f "$TMP_OUT"' EXIT

pass=0; fail=0; skip=0; known=0; timedout=0; stale_known=0
failed_names=""
known_names=""
stale_known_names=""
json_rows=""
nl='
'

# `while read` (not `for x in $(ls)`) so a filename containing SPACES stays one script: the
# `for` form split "dbScript - Copie.slc" into three phantom entries, one of which was a bare "-")
# --profile correctness: skip the suites whose pass/fail depends on wall-clock (benchmarks and stress runs).
# They are not correctness tests, and including them makes iteration pay for throughput measurement. A
# wall-clock number must not be read as a correctness signal, the lesson of crash_hashset_test and
# bench_collections_ops, both of which failed a 20s budget on one run and passed on another.
slow_names=()
if [ "$PROFILE" = "correctness" ] && [ -f "$SLOW_FILE" ]; then
  while IFS= read -r sline; do
    sname="${sline%%#*}"
    sname="${sname%"${sname##*[![:space:]]}"}"
    [ -z "$sname" ] && continue
    set -- $sname
    slow_names+=("$1")
  done < "$SLOW_FILE"
fi

# AOT-only suites (src/build/repl-test-aot-only.txt): robustness probes that assert MILLIONS of nested calls
# survive. True on AOT (32MB stack, memory-derived frame cap), impossible on JIT by design (24-frame cap,
# see src/Interpreter/Interpreter.cs `MaxCallDepth`, `// platform: limit`). Parsed ONCE, before the loop,
# and skipped on every non-AOT platform (jit, jit(fallback), custom) counting into the SAME skip total as
# the other registries, the suites are never deleted because they are the evidence for the AOT claim.
# Only parsed when it can matter (PLATFORM != aot), matching the parse-once discipline of the env file.
aot_only_names=()
if [ "$PLATFORM" != "aot" ] && [ -f "$AOT_ONLY_FILE" ]; then
  while IFS= read -r aline; do
    aname="${aline%%#*}"
    aname="${aname%"${aname##*[![:space:]]}"}"
    [ -z "$aname" ] && continue
    set -- $aname
    aot_only_names+=("$1")
  done < "$AOT_ONLY_FILE"
fi

# Names are exact now (the old matcher allowed globs). Rather than trust that, verify it: an unparsed name
# would become a silently ignored override, the same failure class as everything else this session fixed.
load_suite_overrides
for name in "${!OVR_TIMEOUT[@]}" "${!OVR_ENV[@]}"; do
  [ -n "$(suite_path "$name")" ] || { echo "env file names '$name', which is not a suite in $SUITE_ROOT" >&2; exit 2; }
done

while IFS= read -r script; do
  base="$(basename "$script")"
  if [ -n "$FILTER" ]; then
    case "$base" in *"$FILTER"*) : ;; *) continue ;; esac
  fi
  if skip_match "$base"; then skip=$((skip + 1)); continue; fi
  if [ "$PROFILE" = "correctness" ]; then
    for s in ${slow_names[@]+"${slow_names[@]}"}; do
      [ "$s" = "$base" ] && { skip=$((skip + 1)); continue 2; }
    done
  fi
  if [ "$PLATFORM" != "aot" ]; then
    for a in ${aot_only_names[@]+"${aot_only_names[@]}"}; do
      [ "$a" = "$base" ] && { skip=$((skip + 1)); continue 2; }
    done
  fi

  # Per-suite overrides (usually none). Unquoted on purpose: $suite_env is a VAR=VALUE list.
  # shellcheck disable=SC2086
  suite_timeout="${OVR_TIMEOUT[$base]:-}"
  suite_env="${OVR_ENV[$base]:-}"
  case "$suite_timeout" in
    '' | *[!0-9]*)
      [ -n "$suite_timeout" ] && echo "ignoring non-numeric TIMEOUT for $base: $suite_timeout" >&2
      suite_timeout="$TIMEOUT_S" ;;
  esac
  if [ "$QUIET" -eq 0 ]; then
    [ -n "$suite_env" ] && echo "env         $base  $suite_env"
    [ "$suite_timeout" != "$TIMEOUT_S" ] && echo "timeout     $base  ${suite_timeout}s"
  fi
  # -k kills a script that ignores SIGTERM: without it a single suite that traps or blocks the signal
  # wedges the WHOLE gate (observed 2026-09-10: a full run sat on one script for 15+ minutes past its
  # 20s timeout, producing no progress at all). With -k the script becomes a visible TIMEOUT instead.
  # Print BEFORE running: when the gate wedges, this names the suite it is stuck in rather than the
  # last one that finished, the whole reason progress is now the default.
  [ "$QUIET" -eq 0 ] && echo "run         $base  (timeout ${suite_timeout}s)"
  # `< /dev/null` IS LOAD-BEARING: the loop's stdin is the SUITE LIST (the `done < <(suite_files)` below), so a
  # suite that reads stdin would eat the rest of the run. Measured when the gate became a tree: `tool_pipeline_test`
  # (which starts an engine child that reads its input) drained the pipe and the run stopped at 188 of 409 suites,
  # silently. A non-interactive gate must hand a suite no stdin at all.
  ( cd "$WORKDIR" && env $suite_env timeout -k 5 "$suite_timeout" "$EXE_ABS" "$ROOT/$script" ) < /dev/null > "$TMP_OUT" 2>&1
  code=$?
  status="PASS"
  detail=""
  if [ $code -eq 124 ]; then
    status="TIMEOUT"; detail="exceeded ${suite_timeout}s"; timedout=$((timedout + 1))
  elif [ $code -ne 0 ]; then
    # A suite that DIED has no tally line at all, and `exit=1` alone reads like a failing check. The difference is
    # worth naming: an abort before the first Check prints no failing line either (measured: a suite whose subjects
    # were parsed wrongly died at its SECOND statement and answered `exit=1` with nothing else), which is how a broken
    # suite came to read as OK in a hand-rolled loop over the same output.
    if grep -qm1 -oE '[0-9]+ passed, [0-9]+ failed' "$TMP_OUT" 2>/dev/null; then
      status="FAIL"; detail="exit=$code"
    else
      status="FAIL"; detail="exit=$code (died before any check: no tally line)"
    fi
  else
    # Secondary signals, in order. NOTE: many suites exit 0 even with failing checks (they do not
    # all call exit(CheckFailed())), so the summary line is authoritative. `grep -m1` keeps this
    # cheap and line-oriented, which avoids the false positives a whole-output grep produced
    # (giant single-line JSON). Never grep generically for "failed".
    err="$(grep -m1 '\[ERROR\]' "$TMP_OUT" 2>/dev/null | cut -c1-200)"
    if [ -n "$err" ]; then
      status="FAIL"; detail="$err"
    else
      summary="$(grep -m1 -oE '[0-9]+ passed, [0-9]+ failed' "$TMP_OUT" 2>/dev/null)"
      if [ -n "$summary" ]; then
        failed_n="$(printf '%s' "$summary" | sed -E 's/.* ([0-9]+) failed/\1/')"
        [ "$failed_n" -gt 0 ] && { status="FAIL"; detail="$summary"; }
      else
        failed_n="$(grep -m1 -oE 'Failed: [0-9]+' "$TMP_OUT" 2>/dev/null | sed -E 's/.* ([0-9]+)/\1/')"
        if [ -n "$failed_n" ] && [ "$failed_n" -gt 0 ]; then
          status="FAIL"; detail="Failed: $failed_n"
        elif grep -qm1 'TEST(S) FAILED' "$TMP_OUT" 2>/dev/null; then
          status="FAIL"; detail="TEST(S) FAILED"
        fi
      fi
    fi
  fi

  case "$status" in
    PASS)
      pass=$((pass + 1))
      # A registry entry that no longer fails is a rubber stamp, and nothing else would ever say so: the
      # entry would go on vouching for a suite that is fine. Report it, with the instruction to remove it.
      # Deliberately NOT a gate failure, an entry can be environment-dependent (ffmpeg_test passes where
      # ffmpeg is installed, and process_comprehensive_test's timing check is machine-bound), so failing
      # the run would punish the machines where the entry is simply obsolete.
      if known_fail "$base"; then
        stale_known=$((stale_known + 1)); stale_known_names="$stale_known_names$base$nl"
        [ "$QUIET" -eq 0 ] && echo "known-stale $base  passes now, remove it from $KNOWN_FAIL_FILE"
      fi
      ;;
    *)
      if known_fail "$base"; then
        known=$((known + 1)); known_names="$known_names$base ($status)$nl"
        [ "$QUIET" -eq 0 ] && echo "known-fail  $base  $detail"
      else
        fail=$((fail + 1)); failed_names="$failed_names$base :: $status $detail$nl"
        echo "FAIL        $base  $detail"
      fi
      ;;
  esac
  if [ "$JSON" -eq 1 ]; then
    json_rows="$json_rows{\"script\":\"$base\",\"status\":\"$status\",\"detail\":\"$detail\"},$nl"
  fi
  if [ "$QUIET" -eq 0 ] && [ "$status" = "PASS" ]; then echo "pass        $base"; fi
done < <(suite_files)

total=$((pass + fail + known + skip))

if [ "$JSON" -eq 1 ]; then
  printf '{"runner":"%s","workdir":"%s","total":%d,"passed":%d,"unexpectedFailures":%d,"knownFailures":%d,"skipped":%d,"timedOut":%d,"results":[%s]}\n' \
    "$EXE" "$WORKDIR" "$total" "$pass" "$fail" "$known" "$skip" "$timedout" "$(printf '%s' "$json_rows" | sed 's/,$//')"
else
  echo
  echo "════════ repl test gate ════════"
  echo "runner            : $EXE"
  case "$PLATFORM" in
    aot) echo "platform          : aot (shipping target)" ;;
    *)   echo "platform          : $PLATFORM" ;;
  esac
  echo "working dir       : $WORKDIR"
  echo "scripts scanned   : $total"
  echo "passed            : $pass"
  echo "unexpected failure: $fail"
  echo "known failure     : $known"
  if [ "$timedout" -gt 0 ]; then
    # Printed ONLY when it happened, because a COUNT of timeouts reads like an announcement when it is 0, which
    # is the same shape as the per-suite `timeout <name> <Ns>` budget line (printed before the run) that made a
    # clean run look like eleven failures (2026-10-06). A timeout is already counted in `unexpected failure` (or
    # in `known failure`) and named in the list below; this line is here so the SUMMARY says it too.
    echo "timed out         : $timedout   (counted above AND named in the failure list)"
  fi
  if [ "$stale_known" -gt 0 ]; then
    echo "known but passing : $stale_known   (entries in $KNOWN_FAIL_FILE whose suite now PASSES)"
  fi
  echo "skipped           : $skip"
  if [ -n "$known_names" ]; then
    echo "── known failures (expected, see $KNOWN_FAIL_FILE) ──"
    printf '%s' "$known_names"
  fi
  if [ -n "$stale_known_names" ]; then
    echo "── REGISTRY ENTRIES THAT NOW PASS, remove them from $KNOWN_FAIL_FILE ──"
    printf '%s' "$stale_known_names"
  fi
  if [ -n "$failed_names" ]; then
    echo "── UNEXPECTED FAILURES ──"
    printf '%s' "$failed_names"
  fi
fi

[ "$fail" -eq 0 ] && exit 0 || exit 1
