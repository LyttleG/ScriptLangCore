#!/usr/bin/env bash
# WHICH .slc FILES TOUCH THE NATIVE SURFACE, and exactly what they touch, so a mass update can be aimed.
#
# Two reports. The first is one line per file with a count, which says how big a change would be. The second lists each
# distinct entry with the files that mention it, which says which files a rename or a replacement reaches.
#
# THREE PATTERNS, because the native surface has three doors and a grep for "WinApi" alone sees only the first:
#   WinApi.X          the namespace, a call with a handle passed or a native the engine does not wrap
#   LoadLibrary(...)  a library loaded at RUN TIME, whose exports are reached as members on the result
#   FreeLibrary(...)  and its teardown, which is a builtin rather than an export, so it is spelled the same way
#   GetFunction(...)  the raw-pointer form, for a script that holds a pointer
# The bare LoadLibrary/FreeLibrary matter because they are registered as GLOBALS: a script may reach them with no prefix at
# all, and a pattern that only looked for WinApi. would call that file clean while it loads a native.
#
# Usage: bash src/build/winapi-usage.sh [dir]        (default: assets, i.e. the suites and the samples)
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
scan="${1:-$root/assets}"

if [ ! -d "$scan" ]; then
  echo "not a directory: $scan" >&2
  exit 2
fi

pattern='WinApi\.[A-Za-z_][A-Za-z0-9_]*|LoadLibrary\(|FreeLibrary\(|GetFunction\(|DynamicLibrary'

# Only the script files, which is what a mass update would rewrite. Sorted so two runs diff cleanly.
files="$(grep -rlE "$pattern" --include='*.slc' "$scan" 2>/dev/null | sort)"

if [ -z "$files" ]; then
  echo "no .slc file under $scan touches the native surface"
  exit 0
fi

echo "=== files, with how many native entries each uses ==="
# read, not a for-loop over the expansion: a .slc name may contain a SPACE, and word splitting turned one such file into
# two missing ones and a stray "0 -" row. Measured on this very run, with "dbScript Copie.slc".
printf '%s\n' "$files" | while IFS= read -r f; do
  printf '%5d  %s\n' "$(grep -cE "$pattern" "$f")" "${f#"$root"/}"
done
printf '%5d  %s\n' "$(printf '%s\n' "$files" | grep -cE "$pattern")" "TOTAL entries across $(printf '%s\n' "$files" | wc -l) file(s)"

echo
echo "=== each distinct entry, and the files that mention it ==="
# The member names, not whole lines: what a mass update needs is the SET, and a line is mostly arguments.
printf '%s\n' "$files" | while read -r f; do
  grep -oE 'WinApi\.[A-Za-z_][A-Za-z0-9_]*' "$f" | sed 's/^WinApi\.//' | sort -u | while read -r m; do
    printf '%s\t%s\n' "$m" "${f#"$root"/}"
  done
  for bare in LoadLibrary FreeLibrary GetFunction; do
    if grep -qE "(^|[^.[:alnum:]_])$bare\(" "$f"; then
      printf '%s\t%s\n' "$bare" "${f#"$root"/}"
    fi
  done
done | sort -u
