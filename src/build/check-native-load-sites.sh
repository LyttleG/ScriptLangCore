#!/usr/bin/env bash
# check-native-load-sites.sh, the native load-and-redirect site must exist exactly ONCE.
#
# Why: LoadLibrary was implemented twice in WinApi.cs (:199 for the namespace form, :1610 for the global
# one) with byte-identical bodies. Duplicated logic is this repository's recurring bug: only the copy an
# input happens to reach matters, and the stale twin is invisible until it bites (three times this session).
# NativeLibrary.Load(PreferBesideExe( is the heart of that body, so its count is the honest measure.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
[ -d src ] || { echo "no src/ here, run this from the repository root" >&2; exit 2; }

sites=$(grep -rn "NativeLibrary.Load(PreferBesideExe(" src/ --include=*.cs || true)
count=$(printf '%s\n' "$sites" | grep -c . || true)
if [ "$count" -ne 1 ]; then
  echo "FAIL: the native load-and-redirect site appears $count times; it must appear exactly once:" >&2
  printf '%s\n' "$sites" | sed 's/^/  /' >&2
  echo >&2
  echo "Fix: one private LoadNativeLibrary(args) in WinApi.cs, called by both registrations." >&2
  exit 1
fi
echo "ok: the native load-and-redirect site appears exactly once"
