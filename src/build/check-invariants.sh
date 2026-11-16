#!/usr/bin/env bash
# check-invariants.sh, the rules this repository keeps re-learning, in one command, so obeying them costs
# nothing. Run before a commit that touches error handling, the dispatch paths, or the suite directory.
#
# Each check is a bug class that actually bit: a duplicated implementation drifting from its twin, or a test
# that could go invisible. Grows one check per class (the story is in AGENTS.md) rather than as a big harness.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
fail=0
for check in check-dup-friendly-maps.sh check-dup-bodies.sh check-dead-privates.sh check-dot-commands.sh check-com-registration.sh check-abi-manifest.sh check-suite-accounting.sh check-native-load-sites.sh check-platform-branches.sh check-opcode-coverage.sh check-suite-protocol.sh check-doc-structure.sh check-doc-nesting.sh check-doc-nav.sh check-doc-keywords.sh check-doc-voice.sh check-doc-bridge-c.sh check-type-name-parity.sh check-rules-pins.sh; do
  echo "── $check"
  bash "src/build/$check" || fail=1
done
if [ "$fail" -eq 0 ]; then
  echo "all invariants hold"
else
  echo >&2
  echo "invariant violations above, see AGENTS.md for why they exist" >&2
fi
exit $fail
