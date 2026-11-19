#!/usr/bin/env bash
# build.sh, the NATIVE plugin fixture (`jitplugin_native.c`), built into a DLL the engine loads with LoadLibrary.
#
# THIS FILE IS A WRAPPER, on purpose: `build.bat` beside it is the ONE statement of the toolchain probe, the compile
# line and the export check (it is the counterpart of `src/embed/launcher-c/build.bat`). This script exists so the bash
# tools reach that same build -- `src/build/jitplugin-check.sh` is one -- instead of carrying a second copy of the flags
# that would drift. It converts its argument to a Windows path and hands the whole job to cmd.
#
# Usage: bash src/jitplugins/native/build.sh [output.dll]
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BATCH="$(cygpath -w "$HERE/build.bat" 2>/dev/null || printf '%s' "$HERE/build.bat")"

if [ "$#" -gt 0 ]; then
    OUT="$(cygpath -w "$1" 2>/dev/null || printf '%s' "$1")"
    exec cmd //c "$BATCH" "$OUT"
fi
exec cmd //c "$BATCH"
