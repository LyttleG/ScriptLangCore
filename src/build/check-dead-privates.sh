#!/usr/bin/env bash
# A private member that NOTHING references is either dead weight or a LOST call site, and from the outside
# the two look identical. Measured, which is why this check exists: Vm.cs's HasOutParam looked dead and
# turned out to be the ONE implementation three call sites had stopped using, so the fix was to make them
# use it, not to delete it. This check finds the shape so a human decides which one it is.
#
# What it flags: a private method, field or property whose NAME occurs exactly once in all of src, i.e. only
# where it is declared. The name being absent everywhere else also rules out nameof and a string reference,
# so only a non-literal reflection could reach it, and this engine's reflection is over the script surface.
#
# What it deliberately does NOT flag:
#   * a `private const`, a named native constant, which is documentation of the API rather than dead code
#     (and AGENTS records that an uncalled declaration is kept on purpose);
#   * a `[LibraryImport]` method, because a P/Invoke entry point is not dead with no caller.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

freq=$(mktemp); known=$(mktemp); trap 'rm -f "$freq" "$known"' EXIT
grep -rhoE '[A-Za-z_][A-Za-z0-9_]*' src --include='*.cs' | sort | uniq -c | awk '{print $2, $1}' > "$freq"
# The named exceptions, each with its reason: a check that fails on landing gets switched off, so a member
# whose classification needs a reading rather than a grep goes here (src/build/dead-privates-known.txt).
awk -F'\t' '!/^#/ && NF >= 2 { print $1 }' src/build/dead-privates-known.txt > "$known" 2>/dev/null || true

found=$(find src -name '*.cs' -print0 | sort -z | xargs -0 awk -v freq="$freq" -v known="$known" '
BEGIN {
  while ((getline l < freq) > 0) { split(l, a, " "); cnt[a[1]] = a[2] }
  while ((getline k < known) > 0) { skip[k] = 1 }
}
BEGIN { while ((getline l < freq) > 0) { split(l, a, " "); cnt[a[1]] = a[2] } }
{
  # A 6-line window, because a [LibraryImport] attribute can span several lines above its declaration.
  win = win "\n" $0
  n = split(win, w, "\n")
  if (n > 6) { win = ""; for (i = n - 5; i <= n; i++) win = win "\n" w[i] }
  if ($0 !~ /^[ \t]*private[ \t]/) next
  if ($0 ~ /^[ \t]*private[ \t]+const[ \t]/) next
  if (win ~ /\[LibraryImport/) next
  s = $0
  sub(/^[ \t]*private[ \t]+/, "", s)
  for (k = 0; k < 4; k++) sub(/^(static|async|partial|readonly|const|extern|unsafe|required)[ \t]+/, "", s)
  if (match(s, /[A-Za-z_][A-Za-z0-9_]*[ \t]*[\(=;]/) == 0) next
  name = substr(s, RSTART, RLENGTH)
  sub(/[ \t]*[\(=;]$/, "", name)
  if (name in skip) next
  if (cnt[name] <= 1) print FILENAME ":" FNR ": " name
}' 2>/dev/null)

if [ -n "$found" ]; then
  echo "FAIL: private member(s) referenced by nothing but their own declaration:"
  echo "$found" | sed 's/^/  /'
  echo "  Decide which each one is: dead weight to delete, or a lost call site to restore."
  exit 1
fi
echo "ok: every private member is referenced somewhere beyond its declaration"
