#!/usr/bin/env bash
# check-suite-protocol.sh, every suite that ASSERTS must be able to FAIL.
#
# Why this exists (one line per shape): an audit found SIXTEEN suites that checked things and reported success
# whatever happened. The runner reads the process EXIT CODE, and Check() only counts, so:
#   * CheckSummary() PRINTS the tally and returns a bool; only exit(CheckFailed()) sets the status. Nine suites
#     called Check without ever exiting (iterable_test, forward_ref_test, crash_regex, crash_switch, crash_dict,
#     crash_generator, crash_middleware, vm_overall, vm_switch_pattern); crash_nested_struct and t4_struct kept
#     private counters and printed them.
#   * A suite can define its OWN Check(label, ok), HIDING the builtin (_coro_nested_recursion did exactly that).
#   * A private reporter hides the same thing behind another name (crash_lib_import/merge/badvar used a lowercase
#     check() helper with private counters; crash_lib_types and crash_ctor_vm asserted with
#     print(cond ? "PASS ..." : "FAIL ..."), which can never fail at all).
#   * And some suites ran NO code: json_inc_test and vm_export_runner both began with an unconditional exit(0),
#     for vm_export_runner that also hid the fact that it read blobs its writer had not produced yet.
#   * And a Check() placed AFTER the last CheckSummary() RUNS and prints its own line while changing nothing: the
#     tally the runner reads has already gone out (met for real, two sections appended after CheckSummary() in
#     crash_extension_members.slc reported 63 checks as 56, and the failing lines printed while the suite was
#     judged green; no suite in the tree does it now, which is why the rule needs no exception).
# Every one of those was found by hand, one at a time. This check is that audit, so the class cannot come back.
#
# It is about the PROTOCOL, not about counting checks: a suite with no assertions at all is ALLOWED (the directory
# holds the maintainer's probes and benchmarks too) and is merely reported. Comments are ignored, because a comment
# mentioning exit(0) is not an assertion, the first cut flagged its own explanations.
#
# ONE awk pass rather than a grep per rule per file: 417 files × several greps took minutes, which is not a check
# you can leave in the gate.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
# WHERE THE SUITES ARE is stated once, in _suites.sh (the gate is a tree; the awk below keys bare names,
# which it already did: it strips the directory off FILENAME).
. "$ROOT/src/build/_suites.sh"
SUITES="$ROOT/$SUITE_ROOT"
mapfile -t suite_list < <(suite_files)

awk '
  function reset() { check=0; ex=0; firstCheck=0; firstExit=0; ownCheck=0; passFail=0; lastCheck=0; lastSummary=0; unreadable=0; realExit=0 }
  # A string literal is not code. A suite that writes its SUBJECT program as a script string (one that calls
  # exit(0), which a script legitimately does) is not a suite that exits before its own checks, and the first cut
  # flagged exactly that. Every rule looking for a CALL reads the stripped line; the PASS/FAIL rule reads the RAW
  # one, because there the quoted text IS the signal.
  function strip(s,   out, i, c, inStr) {
    out = ""; inStr = 0
    for (i = 1; i <= length(s); i++) {
      c = substr(s, i, 1)
      if (inStr) { if (c == "\\") { i++ } else if (c == "\"") { inStr = 0 }; continue }
      if (c == "\"") { inStr = 1; continue }
      out = out c
    }
    return out
  }
  FNR == 1 { if (NR > 1) verdict(); reset(); file = FILENAME; sub(/^.*\//, "", file) }
  {
    if ($0 ~ /^[ \t]*\/\//) next          # a comment is not code
    line = strip($0)
    if (line ~ /Check\(/)  { check = 1; if (firstCheck == 0) firstCheck = FNR }
    if (line ~ /exit\(/)   { ex = 1;    if (firstExit == 0)  firstExit  = FNR }
    # An exit that can REPORT: `exit(0)` reaches the runner as "green" whatever the checks said, so a suite
    # whose only exit is a literal 0 is in the same position as one that never exits, for a tally the runner
    # cannot read. Measured on `exit(failures > 0 ? 1 : 0)` and `exit(CheckFailed())`, which both count.
    if (line ~ /exit\(/ && line !~ /exit\([ \t]*0[ \t]*\)/) realExit = 1
    if (line ~ /^[ \t]*(void|bool|int)[ \t]+Check[ \t]*\(/) ownCheck = 1
    if ($0 ~ /\?[ \t]*"PASS[^"]*"[ \t]*:[ \t]*"FAIL/)     passFail = 1
    # A tally the RUNNER CANNOT READ: a pass/fail count pair that is not the "N passed, M failed" shape its
    # classifier matches. crash_network_fixes printed "N pass, M fail" and never exited, so its four failures
    # were invisible to the gate. A suite printing the STANDARD summary is read by the tally and is not this.
    if (tolower($0) ~ /pass[ \t]*[,:\/|-]/ && tolower($0) ~ /fail/ && tolower($0) !~ /passed,/) unreadable = 1
    if (line ~ /Check\(/)          lastCheck   = FNR
    if (line ~ /CheckSummary\(\)/) lastSummary = FNR
  }
  function verdict(   bad) {
    bad = 0
    if (unreadable && !realExit) { printf "  %s: prints a pass/fail tally the runner cannot read (not the \"N passed, M failed\" shape) and its exit (if any) is a literal 0, so its failures are invisible to the gate\n", file; bad = 1 }
    if (ownCheck)     { printf "  %s: declares its own Check(...) , that SHADOWS the builtin and its counters never reach exit()\n", file; bad = 1 }
    if (passFail)     { printf "  %s: asserts with print(cond ? \"PASS ...\" : \"FAIL ...\") , that can never fail\n", file; bad = 1 }
    if (firstCheck && firstExit && firstExit < firstCheck) { printf "  %s: exits (line %d) BEFORE its first Check (line %d) , the checks are dead code\n", file, firstExit, firstCheck; bad = 1 }
    if (lastSummary && lastCheck > lastSummary) { printf "  %s: a Check() at line %d comes AFTER the last CheckSummary() (line %d) - its result never reaches the tally the runner reads", file, lastCheck, lastSummary; print ""; bad = 1 }
    if (bad) failures++
    if (check) asserting++; else probes++
  }
  END { verdict()
        if (failures == 0) printf "ok: every suite that asserts can fail (exit(CheckFailed())); %d asserting, %d assertion-free (probes/benchmarks)\n", asserting, probes
        exit (failures == 0 ? 0 : 1) }
' "${suite_list[@]}"
