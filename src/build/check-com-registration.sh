#!/usr/bin/env bash
# check-com-registration.sh, one ProgID and one CLSID, spelled the same in every place that spells them.
#
# Why this exists. The engine DLL is also an in-proc COM server, and its identity is written down in FOUR files that
# nothing else compares: the constants in `src/Host/ComServer.cs` (what the DLL answers), the harness that proves it
# (`src/embed/Program.cs`), the per-user registration a developer runs (`setup/com-register.reg`) and the installer's
# `[Registry]` section (`setup/ScriptLangCore.iss`). A hand edit to one of them is silent in the worst way: the DLL
# keeps serving its own CLSID, the registry keeps naming a different one, and `CreateObject("ScriptLangCore.Session")`
# answers Windows' "ActiveX component can't create object", which names neither the file nor the mismatch.
#
# Each file spells the GUID its own way (a C# literal, a .reg literal, an Inno `#define`), so this check RESOLVES each
# spelling first and then compares values: comparing raw text would only prove that four different syntaxes differ.
#
# What it checks:
#   1. THE CLSID is one value, resolved from the C# constant, the harness, the .reg and the installer's #define.
#   2. THE PROGID is one value across the three files that name it.
#   3. THE INSTALLER'S THREE KEYS exist in BOTH hives (ProgID, CLSID, InprocServer32), and each of ours carries
#      `uninsdeletekey`: an uninstall must not leave a ProgID pointing at a deleted DLL.
#   4. THE THREADING MODEL is `Apartment` in both registration doors, which is what Office's STA needs.
#   5. THE INPROCSERVER32 PATH is the DLL the installer already ships, and in the .reg it is absolute.
#
# Usage: bash src/build/check-com-registration.sh
# Exit: 0 everything agrees, 1 a mismatch (named, with both values), 2 one of the four files is missing.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

SERVER=src/Host/ComServer.cs
HARNESS=src/embed/Program.cs
REG=setup/com-register.reg
ISS=setup/ScriptLangCore.iss

for f in "$SERVER" "$HARNESS" "$REG" "$ISS"; do
  [ -f "$f" ] || { echo "FAIL: no such file: $f (the COM leg moved, and this check was not told)" >&2; exit 2; }
done

FAILED=0
fail() { echo "FAIL: $1" >&2; FAILED=1; }
# GUIDs are compared case-insensitively: a GUID has no case, and a hand edit may write either.
same_guid() { [ "$(printf '%s' "$1" | tr 'a-f' 'A-F')" = "$(printf '%s' "$2" | tr 'a-f' 'A-F')" ]; }

# ── 1. one CLSID, resolved from each file's own spelling ──────────────────────────────────────────────────────────
server_clsid=$(sed -n 's/.*ClsidText = "\([0-9A-Fa-f-]\{36\}\)".*/\1/p' "$SERVER" | head -1)
harness_clsid=$(sed -n 's/.*var comClsid = new Guid("\([0-9A-Fa-f-]\{36\}\)").*/\1/p' "$HARNESS" | head -1)
reg_clsid=$(grep -m1 -oE 'Software\\Classes\\CLSID\\\{[0-9A-Fa-f-]{36}\}' "$REG" | grep -oE '[0-9A-Fa-f-]{36}' | head -1)
iss_clsid=$(grep -m1 -E '^[[:space:]]*#define[[:space:]]+ComClsid' "$ISS" | grep -oE '[0-9A-Fa-f-]{36}' | head -1)

[ -n "$server_clsid" ] || fail "$SERVER declares no ClsidText constant"
for pair in "$HARNESS:$harness_clsid" "$REG:$reg_clsid" "$ISS:$iss_clsid"; do
  where="${pair%%:*}"; value="${pair#*:}"
  [ -n "$value" ] || { fail "$where names no CLSID this check can read"; continue; }
  same_guid "$value" "$server_clsid" ||
    fail "the CLSID in $where is {$value} and $SERVER says {$server_clsid}"
done

# ── 2. one ProgID ────────────────────────────────────────────────────────────────────────────────────────────────
server_progid=$(sed -n 's/.*ProgId = "\([^"]*\)".*/\1/p' "$SERVER" | head -1)
[ -n "$server_progid" ] || fail "$SERVER declares no ProgId constant"
for f in "$REG" "$ISS"; do
  grep -qF "$server_progid" "$f" || fail "$f never names the ProgID '$server_progid'"
done

# ── 3. the installer's keys, in both hives, and removable ────────────────────────────────────────────────────────
# An Inno entry may continue onto the next line with a trailing backslash, and the `Flags: uninsdeletekey` of every one
# of ours sits on that continuation, so a line-by-line test would report "no key carries the flag" while every key
# does. The continuations are joined first, and the CRLF the file is saved with is stripped, or `$` never matches.
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
sed 's/\r$//' "$ISS" | sed ':a; /\\$/{N; s/\\\n//; ta}' > "$TMP/iss.logical"

# The installer writes the CLSID through its own #define, so the exact key text is the one with `{#ComClsid}` in it.
PROGID_KEY="Software\Classes\\$server_progid"
CLSID_KEY='Software\Classes\CLSID\{#ComClsid}'
INPROC_KEY="$CLSID_KEY\InprocServer32"

for hive in HKCU HKLM; do
  grep -qF "Root: $hive; Subkey: \"$PROGID_KEY\"" "$TMP/iss.logical" ||
    fail "the installer writes no $hive ProgID key for $server_progid"
  grep -qF "Root: $hive; Subkey: \"$CLSID_KEY\"" "$TMP/iss.logical" ||
    fail "the installer writes no $hive CLSID key for {$server_clsid}"
  grep -qF "Root: $hive; Subkey: \"$INPROC_KEY\"" "$TMP/iss.logical" ||
    fail "the installer writes no $hive InprocServer32 for {$server_clsid}"
done

# Every key that is OURS must be removed on uninstall. Each of the three keys is tested by name in each hive, which is
# also what keeps the `ThreadingModel` VALUE entry out of the count: its line repeats the same Subkey text, but it is a
# value under a key that the key's own `uninsdeletekey` already removes.
for key in "$PROGID_KEY" "$CLSID_KEY" "$INPROC_KEY"; do
  for hive in HKCU HKLM; do
    entry=$(grep -F "Root: $hive; Subkey: \"$key\";" "$TMP/iss.logical" | head -1)
    [ -n "$entry" ] || continue          # the existence check above already named this one
    case "$entry" in
      *uninsdeletekey*) : ;;
      *) fail "the $hive key \"$key\" carries no uninsdeletekey, so an uninstall leaves it behind" ;;
    esac
  done
done

# ── 4. the threading model, in both doors ───────────────────────────────────────────────────────────────────────
grep -q '"ThreadingModel" *= *"Apartment"' "$REG" ||
  fail "setup/com-register.reg does not set ThreadingModel=Apartment"
grep -q 'ValueData: "Apartment"' "$ISS" ||
  fail "the installer does not set ThreadingModel=Apartment"

# ── 5. the file the registration names ──────────────────────────────────────────────────────────────────────────
grep -qF '{app}\{#EngineFolder}\{#DllName}' "$ISS" ||
  fail "the installer's InprocServer32 does not point at {app}\\{#EngineFolder}\\{#DllName}, the DLL it ships"
grep -qE '^@="[A-Za-z]:\\\\' "$REG" ||
  fail "setup/com-register.reg does not carry an absolute InprocServer32 path (a bare file name is not registrable)"

[ "$FAILED" -eq 0 ] || exit 1

echo "ok: $server_progid = {$server_clsid} in the server, the harness, the .reg and the installer; all six of our keys are removed on uninstall"
