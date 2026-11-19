#!/usr/bin/env bash
# src/build/java-check.sh, drive the published engine DLL from a JAVA host, the way `bindings/JavaTest.java` does.
#
# WHY THIS IS NOT A SUITE: the suites run INSIDE the engine, so nothing a suite can do loads the engine's own DLL from
# another language's runtime. This instrument is the other side of the boundary, one more carrier over: it compiles the
# generated declarations (`bindings/ScriptLangCore.java`, rendered from docs/embed/abi.json) together with the check
# that mirrors `bindings/BridgeTest.py`, and runs them against the SHIPPED binary rather than against the source that
# produced it. The carrier speaks the JDK's Foreign Function and Memory API (Panama), so there is no JNI, no shim DLL
# and no third-party dependency, which is also why the check needs a JDK 22 or newer: before 22 the API is not final.
#
# USAGE
#   bash src/build/java-check.sh [engine-dll] [java-home]
# Defaults: publish-dll/ScriptLangCore.dll, and the JDK from JAVA_HOME and then from PATH.
#
# THE RULE THIS SHARES WITH THE GATE: an artifact older than src/ does not describe this tree, so it is REFUSED rather
# than measured (the stale-binary trap has produced wrong answers more than once in this repository).
#
# EXIT CODES: 0 every check passed, 1 a check failed (the test says which), 2 refused to start (no DLL, a stale DLL, no
# JDK 22+, a compile failure).
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

. "$ROOT/src/build/_artifacts.sh"

DLL="${1:-publish-dll/ScriptLangCore.dll}"
WANT_HOME="${2:-${JAVA_HOME:-}}"
OUT="$ROOT/bin/java-check"

if [ ! -f "$DLL" ]; then
  echo "no engine DLL at '$DLL'" >&2
  echo "build it first: $SLC_REMEDY_DLL" >&2
  exit 2
fi

if slc_refuse_stale "$DLL" "the engine DLL" "$SLC_REMEDY_DLL" \
     "the Java host is a SEPARATE program, so its sources are pruned from the comparison" "$ROOT"; then
  echo "FAIL: this artifact would answer for a tree it does not contain" >&2
  exit 2
fi

# ── the JDK, which must be 22 or newer ───────────────────────────────────────────────────────────────────────────────
# The Foreign Function and Memory API is FINAL from 22 and RESTRICTED, so an older JDK does not compile this carrier
# and a run without `--enable-native-access` refuses every downcall. Both are named here rather than discovered as a
# confusing compile error or a runtime refusal that reads as a broken library.
if [ -n "$WANT_HOME" ] && [ -x "$WANT_HOME/bin/javac" ]; then
  JAVA="$WANT_HOME/bin/java"
  JAVAC="$WANT_HOME/bin/javac"
else
  JAVA="$(command -v java 2>/dev/null || true)"
  JAVAC="$(command -v javac 2>/dev/null || true)"
fi

if [ -z "$JAVA" ] || [ -z "$JAVAC" ] || [ ! -x "$JAVA" ] || [ ! -x "$JAVAC" ]; then
  echo "no JDK found: set JAVA_HOME, or pass its folder as the second argument" >&2
  echo "this carrier needs a JDK 22 or newer (the Foreign Function and Memory API is final from 22)" >&2
  exit 2
fi

JAVA_VERSION="$("$JAVA" -version 2>&1 | head -1)"
JAVA_MAJOR="$(printf '%s' "$JAVA_VERSION" | sed -n 's/.*version "\([0-9][0-9]*\).*/\1/p')"
if [ -z "$JAVA_MAJOR" ] || [ "$JAVA_MAJOR" -lt 22 ]; then
  echo "this JDK is too old for the carrier: $JAVA_VERSION" >&2
  echo "a JDK 22 or newer is required (the Foreign Function and Memory API is final from 22)" >&2
  exit 2
fi
echo "engine:  $DLL"
echo "java:    $JAVA_VERSION ($JAVA)"

# ── compile, then run ───────────────────────────────────────────────────────────────────────────────────────────────
# The classes land in `bin/java-check`, which is ignored by git, so this check writes nothing into the tree.
rm -rf "$OUT/classes"
mkdir -p "$OUT/classes"

if ! "$JAVAC" -d "$(cygpath -w "$OUT/classes" 2>/dev/null || printf '%s' "$OUT/classes")" \
      bindings/ScriptLangCore.java bindings/JavaTest.java; then
  echo "FAIL: the carrier or its test did not compile (the errors above are the JDK's own)" >&2
  exit 2
fi
echo "ok: bindings/ScriptLangCore.java and bindings/JavaTest.java compiled with $JAVA_MAJOR"

"$JAVA" --enable-native-access=ALL-UNNAMED \
  -cp "$(cygpath -w "$OUT/classes" 2>/dev/null || printf '%s' "$OUT/classes")" \
  JavaTest "$(cygpath -w "$DLL" 2>/dev/null || printf '%s' "$DLL")"
status=$?

if [ "$status" -eq 0 ]; then
  echo "ok: the Java carrier answered every check against '$DLL'"
else
  echo "FAIL: the Java carrier answered $status (see the failing line above)" >&2
fi

exit "$status"
