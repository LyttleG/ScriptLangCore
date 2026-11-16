#!/usr/bin/env bash
# check-platform-parity.sh, run the gate on BOTH builds and compare them suite by suite.
#
# Why this lives outside the runner: `--build both` runs both builds but never compares them, so a behavioural
# divergence that is not a message goes unnoticed by design, which is how an AOT-only guard hid a JIT hazard
# for as long as it did. And the runner refuses `--build both --json` on purpose ("JSON output is single-build
# by design"), because the parent re-invokes itself per platform. So the comparison belongs here.
#
# Usage:
#   bash src/build/check-platform-parity.sh [--filter NAME] [--allow-stale]
#   bash src/build/check-platform-parity.sh --compare JIT.json AOT.json     # comparison only (used by its self-test)
#
# Exit: 0 both builds agree (or agree after the allow-list), 1 a divergence, 2 the run could not be taken.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

ALLOW_FILE="src/build/repl-test-platform-diff.txt"
AOT_ONLY_FILE="src/build/repl-test-aot-only.txt"
FILTER=""
ALLOW_STALE=""
COMPARE=0
JIT_JSON=""
AOT_JSON=""
while [ $# -gt 0 ]; do
  case "$1" in
    --filter)      FILTER="${2:-}"; shift 2 ;;
    --allow-stale) ALLOW_STALE="--allow-stale"; shift ;;
    --compare)     COMPARE=1; JIT_JSON="${2:-}"; AOT_JSON="${3:-}"; shift 3 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ "$COMPARE" -eq 0 ]; then
  TMPDIR_PARITY="$(mktemp -d)"
  trap 'rm -rf "$TMPDIR_PARITY"' EXIT
  JIT_JSON="$TMPDIR_PARITY/jit.json"
  AOT_JSON="$TMPDIR_PARITY/aot.json"
  extra=()
  [ -n "$FILTER" ] && extra=(--filter "$FILTER")

  # --quiet so stdout is the JSON object alone (progress lines would otherwise precede it).
  bash src/build/run-repl-tests.sh --build jit --quiet --json "${extra[@]+"${extra[@]}"}" > "$JIT_JSON" 2>/dev/null || true
  if ! grep -q '^{' "$JIT_JSON" 2>/dev/null; then
    echo "could not take the JIT run, is bin/Debug/net11.0/ScriptLangCore.exe built?" >&2
    sed 's/^/  /' "$JIT_JSON" 2>/dev/null | head -4 >&2
    exit 2
  fi
  bash src/build/run-repl-tests.sh --build aot --quiet --json $ALLOW_STALE "${extra[@]+"${extra[@]}"}" > "$AOT_JSON" 2>/dev/null || true
  if ! grep -q '^{' "$AOT_JSON" 2>/dev/null; then
    echo "could not take the AOT run (a stale publish is refused by design):" >&2
    sed 's/^/  /' "$AOT_JSON" 2>/dev/null | head -4 >&2
    echo "  republish first: dotnet publish -c Release -v q --nologo" >&2
    exit 2
  fi
fi

python3 - "$JIT_JSON" "$AOT_JSON" "$ALLOW_FILE" "$AOT_ONLY_FILE" <<'PYEOF'
import json, sys

def load(path):
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line.startswith("{"):
                data = json.loads(line)
                return {r["script"]: r["status"] for r in data.get("results", [])}
    print(f"no JSON object in {path}")
    sys.exit(2)

allow = {}
try:
    for line in open(sys.argv[3], encoding="utf-8"):
        line = line.split("#")[0].strip()
        if line:
            allow[line.split()[0]] = True
except FileNotFoundError:
    pass

# AOT-only suites are the DECLARED legitimate divergence: run-repl-tests.sh skips them on non-AOT
# platforms, so they legitimately appear only in the AOT run. Honouring the registry here keeps
# "both builds must agree" a check on SILENT divergence, not a ban on justified ones.
aot_only = {}
try:
    for line in open(sys.argv[4], encoding="utf-8"):
        line = line.split("#")[0].strip()
        if line:
            aot_only[line.split()[0]] = True
except FileNotFoundError:
    pass

jit, aot = load(sys.argv[1]), load(sys.argv[2])
shared = sorted(set(jit) & set(aot))
diff = [(s, jit[s], aot[s]) for s in shared if jit[s] != aot[s] and s not in allow and s not in aot_only]
only = [(s, "jit-only") for s in sorted(set(jit) - set(aot)) if s not in aot_only] + \
       [(s, "aot-only") for s in sorted(set(aot) - set(jit)) if s not in aot_only]

if diff or only:
    print("FAIL: the two builds disagree about these suites:")
    for s, a, b in diff:
        print(f"  {s}: jit={a} aot={b}")
    for s, w in only:
        print(f"  {s}: {w}")
    print("  Either fix the divergence, or add the suite to src/build/repl-test-platform-diff.txt (may differ) / src/build/repl-test-aot-only.txt (AOT-only, skipped on JIT) with a reason.")
    sys.exit(1)
print(f"ok: jit and aot agree on all {len(shared)} shared suites ({len(allow)} allow-listed)")
PYEOF
