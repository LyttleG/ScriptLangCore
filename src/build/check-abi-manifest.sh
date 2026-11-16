#!/usr/bin/env bash
# check-abi-manifest.sh, one description of the embed ABI, and every place that spells it.
#
# Why this exists. `ScriptLangCore.dll` is called from C, C#, Python, PowerShell and VBA, and before this check every
# one of those hosts re-declared the exports by hand: the C launcher's GetProcAddress table, the harness's delegate
# types in `src/embed/Program.cs`, the samples on `docs/embed/howto.html`. Nothing compared them, so a signature could
# change in the engine and the hosts would keep calling the old shape: a missing parameter, a handle passed as an int,
# a buffer read where one was expected. `docs/embed/abi.json` is now the ONE statement of the contract, the files under
# `bindings/` are rendered from it, and this check is what keeps the three honest.
#
# What it checks:
#   1. THE REVISION is one number: the manifest's `abi` and `AbiRevision` in `src/Host/EmbedApi.cs`. A host reads it
#      through `slc_abi_revision`, so a mismatch means the published number is not the one the code answers.
#   2. THE EXPORT SET is the same in both directions: every `[UnmanagedCallersOnly(EntryPoint = "slc_...")]` is in
#      the manifest, and every manifest entry is an export. A name in one and not the other is a host calling into
#      nothing (or a documented call that does not exist).
#   3. EVERY SIGNATURE agrees in SHAPE: the return and each parameter are reduced to int / int64 / pointer on both
#      sides and compared in order, so a parameter added, dropped, reordered or changed from a handle to an int is a
#      failure. Const-ness is NOT compared, because the manifest states it as `direction` and the C# spelling of a
#      caller-owned buffer (`byte*`) is the same for an in and an out parameter.
#   4. THE CAPABILITY BITS agree: `EmbedCapabilities` and the manifest name the same bits at the same values, no bit is
#      used twice, and every bit is actually advertised by `Bits()` rather than only declared.
#   5. THE GENERATED BINDINGS are current: `src/build/gen-abi-bindings.ps1 -Check` re-renders them from the manifest and
#      fails when a committed file differs, which is what stops a hand edit of a generated header.
#
# Usage: bash src/build/check-abi-manifest.sh
# Exit: 0 everything agrees, 1 a mismatch (named, with both values), 2 a file the check needs is missing, or the
#       manifest is not shaped the way this check reads it.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

MANIFEST=docs/embed/abi.json
API=src/Host/EmbedApi.cs
CAPS=src/Host/EmbedCapabilities.cs
GENERATOR=src/build/gen-abi-bindings.ps1

for f in "$MANIFEST" "$API" "$CAPS" "$GENERATOR"; do
  [ -f "$f" ] || { echo "FAIL: no such file: $f (the ABI moved, and this check was not told)" >&2; exit 2; }
done

FAILED=0
fail() { echo "FAIL: $1" >&2; FAILED=1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ── the manifest, read as records ────────────────────────────────────────────────────────────────────────────────
# One record per line, so the comparison is between VALUES rather than between two syntaxes:
#   exports:      name|return|param,param,...|since      (C types, from `exports`)
#   capabilities: name|bit|since                          (from `capabilities`)
# A record starts at an entry's own `"name"` line (six spaces, which is what tells it apart from a parameter's name
# inside a params object) and is printed when `since` arrives, the field every entry ends its numbers with.
awk '
  /"exports": \[/ { inexports = 1; next }
  inexports && /"capabilities": \[/ { inexports = 0 }
  inexports && /^      "name": "/ { line = $0; sub(/.*"name": "/, "", line); sub(/".*/, "", line); name = line; ret = ""; types = ""; next }
  inexports && /^      "returns": "/ { line = $0; sub(/.*"returns": "/, "", line); sub(/".*/, "", line); ret = line; next }
  inexports && /"type": "/ { line = $0; sub(/.*"type": "/, "", line); sub(/".*/, "", line); types = (types == "" ? line : types "," line); next }
  inexports && /^      "since": / {
    line = $0; sub(/.*"since": /, "", line); sub(/[^0-9].*/, "", line)
    print name "|" ret "|" types "|" line
    next
  }
' "$MANIFEST" > "$TMP/manifest.exports"

awk '
  /"capabilities": \[/ { incaps = 1; next }
  incaps && /^  \],?$/ { incaps = 0; next }
  incaps && /^      "name": "/ { line = $0; sub(/.*"name": "/, "", line); sub(/".*/, "", line); name = line; next }
  incaps && /^      "bit": / { line = $0; sub(/.*"bit": /, "", line); sub(/[^0-9].*/, "", line); bit = line; next }
  incaps && /^      "since": / {
    line = $0; sub(/.*"since": /, "", line); sub(/[^0-9].*/, "", line)
    print name "|" bit "|" line
    next
  }
' "$MANIFEST" > "$TMP/manifest.caps"

# The count guards: this check reads a shape, and a reformatted manifest must fail loudly rather than compare two empty
# lists and report success.
manifest_names=$(grep -c '^      "name": "slc_' "$MANIFEST")
export_records=$(grep -c . "$TMP/manifest.exports")
[ "$manifest_names" -eq "$export_records" ] ||
  fail "the manifest's shape is not what this check reads: $manifest_names entries and $export_records export records (an indentation or a field order changed)"
cap_records=$(grep -c . "$TMP/manifest.caps")
grep -q '"capabilities": \[' "$MANIFEST" || fail "the manifest has no capabilities array"
[ "$cap_records" -gt 0 ] || fail "the manifest states no capability bit"

manifest_abi=$(sed -n 's/^  "abi": \([0-9][0-9]*\),$/\1/p' "$MANIFEST" | head -1)
[ -n "$manifest_abi" ] || fail "the manifest states no \"abi\" revision this check can read"

# ── 1. one revision ─────────────────────────────────────────────────────────────────────────────────────────────
code_abi=$(sed -n 's/.*internal const int AbiRevision = \([0-9][0-9]*\);/.\1/p' "$API" | sed 's/^\.//' | head -1)
[ -n "$code_abi" ] || fail "$API declares no AbiRevision constant"
[ -n "$manifest_abi" ] && [ -n "$code_abi" ] && [ "$manifest_abi" != "$code_abi" ] &&
  fail "the manifest says ABI $manifest_abi and $API says $code_abi"

# ── 2 and 3. the export set, and every signature's shape ────────────────────────────────────────────────────────
# The C# types and the C types are reduced to the same three words, so the comparison tests the declaration a host
# compiles against rather than the spelling.
shape_csharp() {
  case "$1" in
    int) echo int ;;
    long) echo int64 ;;
    "byte*"|"nint"|"IntPtr") echo ptr ;;
    *) echo "unknown:$1" ;;
  esac
}
shape_c() {
  case "$1" in
    int) echo int ;;
    int64_t) echo int64 ;;
    *"*") echo ptr ;;
    *) echo "unknown:$1" ;;
  esac
}

# From the source: `name|returnShape|shape,shape` one record per export, reading the [UnmanagedCallersOnly] line and
# the `public static ...` declaration that follows it.
awk '
  /EntryPoint = "slc_/ {
    line = $0; sub(/.*EntryPoint = "/, "", line); sub(/".*/, "", line); name = line; next
  }
  name != "" && /public static / {
    line = $0
    sub(/.*public static /, "", line)
    ret = line; sub(/ .*/, "", ret)
    params = ""
    # The FIRST parenthesised group is the parameter list: a method whose body is an expression (`=> Bits();`) has
    # more parentheses after it, and a greedy match would read them as parameters.
    open = index(line, "(")
    if (open > 0) {
      rest = substr(line, open + 1)
      ends = index(rest, ")")
      if (ends > 0) {
        params = substr(rest, 1, ends - 1)
        # Parameters are `Type name`, comma separated; the TYPE is everything before the last space.
        n = split(params, decls, ", ")
        types = ""
        for (i = 1; i <= n; i++) {
          if (decls[i] == "") continue
          split(decls[i], words, " ")
          types = (types == "" ? words[1] : types "," words[1])
        }
        params = types
      }
    }
    print name "|" ret "|" params
    name = ""
  }
' "$API" > "$TMP/code.exports"

# A declaration that spans two lines would break the read above, so it is caught here rather than becoming a silent
# "no exports found".
code_export_count=$(grep -c 'EntryPoint = "slc_' "$API")
code_record_count=$(grep -c . "$TMP/code.exports")
[ "$code_export_count" -eq "$code_record_count" ] ||
  fail "$API has $code_export_count exports and this check read $code_record_count: an export's declaration is wrapped over lines"

: > "$TMP/manifest.exports.normalized"
while IFS='|' read -r name ret params since; do
  [ -n "$name" ] || continue
  shaped_params=""
  if [ -n "$params" ]; then
    IFS=',' read -ra list <<< "$params"
    for p in "${list[@]}"; do
      shaped_params="${shaped_params:+$shaped_params,}$(shape_c "$p")"
    done
  fi
  echo "$name|$(shape_c "$ret")|$shaped_params" >> "$TMP/manifest.exports.normalized"
done < "$TMP/manifest.exports"

: > "$TMP/code.exports.normalized"
while IFS='|' read -r name ret params; do
  [ -n "$name" ] || continue
  shaped_params=""
  if [ -n "$params" ]; then
    IFS=',' read -ra list <<< "$params"
    for p in "${list[@]}"; do
      shaped_params="${shaped_params:+$shaped_params,}$(shape_csharp "$p")"
    done
  fi
  echo "$name|$(shape_csharp "$ret")|$shaped_params" >> "$TMP/code.exports.normalized"
done < "$TMP/code.exports"

sort "$TMP/manifest.exports.normalized" > "$TMP/manifest.exports.sorted"
sort "$TMP/code.exports.normalized" > "$TMP/code.exports.sorted"

while IFS='|' read -r name rest; do
  [ -n "$name" ] || continue
  grep -q "^$name|" "$TMP/code.exports.sorted" ||
    fail "the manifest describes '$name', which no [UnmanagedCallersOnly] export in $API provides"
done < "$TMP/manifest.exports.sorted"
while IFS='|' read -r name rest; do
  [ -n "$name" ] || continue
  grep -q "^$name|" "$TMP/manifest.exports.sorted" ||
    fail "$API exports '$name', which $MANIFEST does not describe"
done < "$TMP/code.exports.sorted"

# Shapes are compared only where both sides name the export, so the two lines above report a missing entry once rather
# than twice (as "missing" and again as a signature mismatch).
while IFS='|' read -r name ret params; do
  [ -n "$name" ] || continue
  theirs=$(grep "^$name|" "$TMP/code.exports.sorted" | head -1)
  [ -n "$theirs" ] || continue
  [ "$theirs" = "$name|$ret|$params" ] && continue
  fail "signature of '$name': the manifest says ($ret) returns ($params), $API says ($(echo "$theirs" | cut -d'|' -f2)) returns ($(echo "$theirs" | cut -d'|' -f3))"
done < "$TMP/manifest.exports.sorted"

# ── 4. the capability bits ──────────────────────────────────────────────────────────────────────────────────────
# `internal const int Name = 1 << N;` in EmbedCapabilities, compared with the manifest's name and bit. The manifest
# spells a capability lowercase and the C# spells it PascalCase, so the comparison is on the bit's VALUE and on the
# name folded to one case.
sed -n 's/.*internal const int \([A-Za-z][A-Za-z0-9]*\) = 1 << \([0-9][0-9]*\);/\1|\2/p' "$CAPS" |
  tr '[:upper:]' '[:lower:]' | sort > "$TMP/code.caps"
sort "$TMP/code.caps" > "$TMP/code.caps.sorted"

cut -d'|' -f1,2 "$TMP/manifest.caps" | sort > "$TMP/manifest.caps.sorted"

while IFS='|' read -r name bit; do
  [ -n "$name" ] || continue
  grep -qx "$name|$bit" "$TMP/code.caps.sorted" ||
    fail "capability '$name' is bit $bit in the manifest and $(grep "^$name|" "$TMP/code.caps.sorted" | cut -d'|' -f2 | head -1 || true) in $CAPS"
done < "$TMP/manifest.caps.sorted"
while IFS='|' read -r name bit; do
  [ -n "$name" ] || continue
  grep -qx "$name|$bit" "$TMP/manifest.caps.sorted" ||
    fail "$CAPS declares capability '$name' at bit $bit, which $MANIFEST does not describe"
done < "$TMP/code.caps.sorted"

duplicate_bits=$(cut -d'|' -f2 "$TMP/code.caps.sorted" | sort | uniq -d)
[ -z "$duplicate_bits" ] ||
  fail "two capability bits share a value in $CAPS: $(echo "$duplicate_bits" | tr '\n' ' ')"

# A bit that is declared but never advertised is a promise nobody can read: every constant must also appear inside
# Bits(), which is the only thing `slc_capabilities` answers.
sed -n '/internal static int Bits()/,/^    }/p' "$CAPS" > "$TMP/bits.body"
[ -s "$TMP/bits.body" ] || fail "$CAPS has no Bits() body this check can read"
while IFS='|' read -r name bit; do
  [ -n "$name" ] || continue
  constant=$(sed -n "s/.*internal const int \([A-Za-z][A-Za-z0-9]*\) = 1 << $bit;/\1/p" "$CAPS" | head -1)
  [ -n "$constant" ] || continue
  grep -q "\b$constant\b" "$TMP/bits.body" ||
    fail "capability '$constant' is declared but Bits() never advertises it, so no host can see it"
done < "$TMP/code.caps.sorted"

# ── 5. the manifest is self-consistent, and the bindings are current ───────────────────────────────────────────
if [ -n "$manifest_abi" ]; then
  while IFS='|' read -r name ret params since; do
    [ -n "$name" ] || continue
    [ "$since" -le "$manifest_abi" ] ||
      fail "'$name' says since ABI $since, but the manifest describes ABI $manifest_abi"
  done < "$TMP/manifest.exports"
  while IFS='|' read -r name bit since; do
    [ -n "$name" ] || continue
    [ "$since" -le "$manifest_abi" ] ||
      fail "capability '$name' says since ABI $since, but the manifest describes ABI $manifest_abi"
  done < "$TMP/manifest.caps"
fi

# THE GENERATED FILES: the generator re-renders them in memory and compares, so this check never needs to know what a
# binding looks like. It runs here rather than in its own invariant because a stale binding is this check's subject.
if command -v powershell >/dev/null 2>&1; then
  SHELL_PS=powershell
elif command -v pwsh >/dev/null 2>&1; then
  SHELL_PS=pwsh
else
  fail "no PowerShell to re-render the bindings with ($GENERATOR needs it); install pwsh or run this check on Windows"
  SHELL_PS=""
fi
if [ -n "$SHELL_PS" ]; then
  if ! "$SHELL_PS" -NoProfile -ExecutionPolicy Bypass -File "$GENERATOR" -Check > "$TMP/generator.log" 2>&1; then
    sed 's/^/  /' "$TMP/generator.log" >&2
    fail "the files under bindings/ are not what $MANIFEST renders: edit the manifest and regenerate"
  fi
fi

[ "$FAILED" -eq 0 ] || exit 1

export_count=$(grep -c . "$TMP/code.exports.sorted")
cap_count=$(grep -c . "$TMP/code.caps.sorted")
echo "ok: ABI $manifest_abi, $export_count exports and $cap_count capability bits agree across $MANIFEST, $API, $CAPS and bindings/"
