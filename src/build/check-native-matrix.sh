#!/usr/bin/env bash
# check-native-matrix.sh, the DEPLOYMENT dimension of native loading, which ordinary suites cannot cover.
#
# The engine's nastiest native bug only existed in a deployed layout: loading the same native twice from two
# paths put two instances in the process and the AOT binary died with an access violation (c0000005, traced
# with cdb). A suite that runs in one layout would never have shown it. This exercises the shipped layout on
# purpose, native present, absent, given a foreign path, and duplicated, and asserts the outcome is always a
# clean script error, never a process death.
#
# Needs the AOT publish (the shipping target). Exit: 0 all cases pass, 1 a case failed, 2 no publish.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

PUB="bin/Release/net11.0/win-x64/publish"
EXE="$PUB/ScriptLangCore.exe"
NATIVE="sqlite3.dll"
[ -x "$EXE" ] || { echo "no AOT publish at $EXE, run: dotnet publish -c Release -v q --nologo" >&2; exit 2; }
[ -f "$PUB/$NATIVE" ] || { echo "$NATIVE is not deployed beside $EXE, the matrix cannot run" >&2; exit 2; }

TMP="$(mktemp -d)"
restore() { [ -f "$PUB/$NATIVE.off" ] && mv -f "$PUB/$NATIVE.off" "$PUB/$NATIVE"; rm -rf "$TMP"; }
trap restore EXIT

# Output goes to a FILE, not a command substitution: `out=$(run …)` would run this in a subshell and the
# RC= line would never reach the caller, which is how this harness failed the first time. (Initialising RC=0
# would have "fixed" that by silently disabling case 4's crash detector, which is far worse.)
run() {   # $1 = script → output in $TMP/out.txt, exit code in RC (a native crash shows as non-zero)
  ( cd "$PUB" && timeout -k 5 60 ./ScriptLangCore.exe "$1" 2>&1 ) > "$TMP/out.txt"
  RC=$?
}
grab() { out=$(cat "$TMP/out.txt"); }

pass=0; fail=0
check() {  # $1 = case name, $2 = 0/1 ok, $3 = detail
  if [ "$2" = 1 ]; then echo "  ok   $1"; pass=$((pass + 1))
  else echo "  FAIL $1, $3"; fail=$((fail + 1)); fi
}

echo "── native deployment matrix ($EXE)"

# 1. present: the ordinary case must simply work.
cat > "$TMP/present.slc" <<'EOF'
var db = Sqlite.OpenDb(":memory:");
print("OPENED");
EOF
run "$TMP/present.slc"; grab
case "$out" in *OPENED*) ok=1 ;; *) ok=0 ;; esac
check "1. native present → opens (rc=$RC)" "$ok" "$(printf '%s' "$out" | tail -1 | cut -c1-120)"

# 2. absent: must be a catchable script error naming the problem, never a raw CLR key and never a death.
mv -f "$PUB/$NATIVE" "$PUB/$NATIVE.off"
cat > "$TMP/absent.slc" <<'EOF'
print("STARTED");
try { var db = Sqlite.OpenDb(":memory:"); print("UNEXPECTED_SUCCESS"); }
catch (var ex) { print("CAUGHT: " + ex.Message); }
print("SURVIVED");
EOF
run "$TMP/absent.slc"; grab
ok=1; detail=""
case "$out" in *STARTED*) ;; *) ok=0; detail="did not reach the try, the process died (rc=$RC)"; esac
case "$out" in *CAUGHT:*) ;; *) ok=0; [ -z "$detail" ] && detail="the failure was not catchable"; esac
case "$out" in *SURVIVED*) ;; *) ok=0; [ -z "$detail" ] && detail="the process did not survive (rc=$RC)"; esac
case "$out" in *DllNotFound_Windows*) ok=0; detail="raw CLR resource key reached the script"; esac
case "$out" in *"native library"*) ;; *) ok=0; [ -z "$detail" ] && detail="message does not name the problem"; esac
check "2. native absent → catchable, clear, survived (rc=$RC)" "$ok" "$detail"
mv -f "$PUB/$NATIVE.off" "$PUB/$NATIVE"

# 3. a wrong path: must be a catchable error naming the path we asked for.
mkdir -p "$TMP/nowhere"
cat > "$TMP/wrongpath.slc" <<EOF
try { var lib = LoadLibrary("$TMP/nowhere/libnope.dll"); print("UNEXPECTED_SUCCESS"); }
catch (var ex) { print("CAUGHT: " + ex.Message); }
print("SURVIVED");
EOF
run "$TMP/wrongpath.slc"; grab
ok=1; detail=""
case "$out" in *CAUGHT:*) ;; *) ok=0; detail="not catchable (rc=$RC)" ;; esac
case "$out" in *libnope.dll*) ;; *) ok=0; [ "$ok" = 1 ] && detail="message does not name the requested path" ;; esac
case "$out" in *SURVIVED*) ;; *) ok=0; [ "$ok" = 1 ] && detail="did not survive (rc=$RC)" ;; esac
check "3. wrong path → catchable, names the path (rc=$RC)" "$ok" "$detail"

# 4. duplicated: the same native reachable from a second directory must not become a second instance.
mkdir -p "$TMP/elsewhere"
cp -f "$PUB/$NATIVE" "$TMP/elsewhere/$NATIVE"
cat > "$TMP/duplicate.slc" <<EOF
var lib = LoadLibrary("$TMP/elsewhere/$NATIVE");
var db = Sqlite.OpenDb(":memory:");
print("DUPLICATE_OK");
EOF
run "$TMP/duplicate.slc"; grab
case "$out" in *DUPLICATE_OK*) ok=1 ;; *) ok=0 ;; esac
# a native access violation shows up as a non-zero exit (3221225477 / 139), not as an error message
[ "$RC" = 0 ] || ok=0
check "4. same native from a foreign path → one instance, no crash (rc=$RC)" "$ok" \
  "$(printf '%s' "$out" | tail -1 | cut -c1-120)"

echo "── $pass passed, $fail failed"
[ "$fail" = 0 ] || { echo "a deployment layout is broken, see the cases above" >&2; exit 1; }
echo "ok: native loading behaves in every deployment layout tested"
