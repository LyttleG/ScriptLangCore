#!/usr/bin/env bash
# jitplugin-check.sh, the pin for the JIT-plugin loader (`src/Host/JitPlugins/`).
#
# It builds the FIXTURE (an ordinary JIT class library, the plugin side), publishes the CHECK (a NativeAOT host, the
# loader's side), stages what the harness needs at run time (`nethost.dll` and the fixture with its own
# runtimeconfig.json), and runs it. The harness's exit code is the verdict.
#
# WHY THIS IS A SCRIPT AND NOT A SUITE: the plugin needs its OWN JIT runtime, which a suite cannot start (a suite runs
# INSIDE the engine, and the engine on the shipping build is NativeAOT with `LoadAssembly` refused by design). It is
# the same reason `embed-check.sh` exists, one boundary over.
#
# Usage: bash src/build/jitplugin-check.sh [-c Debug|Release]
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
CONFIG="${1:-Release}"
CONFIG="${CONFIG#-c}"   # tolerate "-c Release" as well as "Release"
[ -n "$CONFIG" ] || CONFIG=Release
OUT="$ROOT/bin/jitplugin-check"

echo "── building the fixture plugin ($CONFIG)"
dotnet build src/jitplugins/Fixture/JitPluginFixture.csproj -c "$CONFIG" -v q --nologo || { echo "FAIL: the fixture did not build" >&2; exit 1; }

echo "── publishing the AOT host ($CONFIG)"
dotnet publish src/jitplugins/Check/JitPluginCheck.csproj -c "$CONFIG" -v q --nologo -o "$OUT" || { echo "FAIL: the host did not publish" >&2; exit 1; }

# nethost.dll is the shipped way to find hostfxr, and a NativeAOT app does not carry it: it comes from the apphost
# pack, so the newest installed one is staged beside the host.
PACKS="/c/Program Files/dotnet/packs/Microsoft.NETCore.App.Host.win-x64"
PACK=""
if [ -d "$PACKS" ]; then PACK="$(ls -d "$PACKS"/* 2>/dev/null | sort -V | tail -1)"; fi
if [ -z "$PACK" ] || [ ! -f "$PACK/runtimes/win-x64/native/nethost.dll" ]; then
  echo "FAIL: nethost.dll was not found under '$PACKS' (install a .NET SDK, or stage nethost.dll beside the harness)" >&2
  exit 2
fi
cp "$PACK/runtimes/win-x64/native/nethost.dll" "$OUT/"

mkdir -p "$OUT/fixture"
cp "src/jitplugins/Fixture/bin/$CONFIG/net11.0/JitPluginFixture.dll" "$OUT/fixture/"
cp "src/jitplugins/Fixture/JitPluginFixture.runtimeconfig.json" "$OUT/fixture/"

echo "── running the harness"
"$OUT/JitPluginCheck.exe"
harness=$?
if [ "$harness" -ne 0 ]; then
  echo "FAIL: the loader harness answered $harness, so the composition legs below would measure a broken door" >&2
  exit "$harness"
fi

# ── THE COMPOSITION: what a SCRIPT sees ──────────────────────────────────────────────────────────────────────────
# The harness above measures the DOOR from C# (`IPluginBridge.Call`). This half measures the reason a plugin is wired
# into a session at all: `Bridge.Call(verb, params)` / `Bridge.CallEnvelope(verb, params)` in a script reach the plugin's
# `Invoke`, with the engine writing the envelope's `v`, `m` and `p`, so no probe below builds a document by hand. THE
# ENGINE HERE IS THE DEBUG BUILD ON PURPOSE: the interesting half of the AOT story (an
# AOT host reaching JIT code) is what the harness's own checks measure, and the composition is envelope plumbing that
# is engine-agnostic, so publishing the engine a second time here would buy nothing and cost minutes.
echo "── building the engine (Debug) for the composition legs"
dotnet build ScriptLangCore.csproj -c Debug -v q --nologo || { echo "FAIL: the engine did not build" >&2; exit 1; }
ENGINE="$ROOT/bin/Debug/net11.0/ScriptLangCore.exe"
[ -f "$ENGINE" ] || { echo "FAIL: no Debug engine at '$ENGINE'" >&2; exit 1; }

# nethost.dll goes BESIDE THE PLUGIN here, which is the other place `PluginSession` looks: a plugin folder is what a
# reader copies around, and the composition must not require it beside the engine as well.
SESSION="$OUT/plugin-session"
mkdir -p "$SESSION"
cp "src/jitplugins/Fixture/bin/$CONFIG/net11.0/JitPluginFixture.dll" "$SESSION/"
cp "src/jitplugins/Fixture/JitPluginFixture.runtimeconfig.json" "$SESSION/"
cp "$PACK/runtimes/win-x64/native/nethost.dll" "$SESSION/"
# The subject asks the three questions this pin exists for: is a door installed, does a verb answer with the plugin's
# own value, and does the plugin's refusal arrive as the envelope's `e.code`. It uses the VERB LAYER, so the same probe
# measures the calling form the sample teaches: `Bridge.CallEnvelope(verb, params)` answers the whole envelope, which is how a
# refusal is READ (`ok`/`e.code`) beside the value (`r`). The `exit(1)` is the check the shell could not make from a
# printed line alone: a plugin that answered 41 would print a line and still fail here.
cat > "$SESSION/probe.slc" <<'SLC'
print("hosted=" + Bridge.Hosted);
var answer = Bridge.CallEnvelope("add", { a: 20, b: 22 });
print("ok=" + answer.Ok + " r=" + answer.Value);
if (answer.Value != 42) { exit(1); }
var refused = Bridge.CallEnvelope("nope", {});
print("bad=" + refused.Ok + " code=" + refused.Error.Code);
SLC

compose_fail() { # $1 what went wrong, $2 the file whose output explains it
  echo "FAIL: $1" >&2
  echo "  output was:" >&2
  sed 's/^/    /' "$2" >&2
  exit 1
}

echo "── a script reaches the plugin through the bridge (a file run)"
"$ENGINE" --plugin "$SESSION/JitPluginFixture.dll" JitPluginFixture.Entry "$SESSION/probe.slc" > "$SESSION/file.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "the run exited $rc, and its exit(1) is the subject's own verdict on the answer" "$SESSION/file.txt"
grep -qF 'hosted=True' "$SESSION/file.txt" || compose_fail "Bridge.Hosted did not answer true, so no door reached the run" "$SESSION/file.txt"
grep -qF 'ok=True r=42' "$SESSION/file.txt" || compose_fail "the script did not read the plugin's answer (expected 'ok=True r=42')" "$SESSION/file.txt"
grep -qF 'bad=False code=UnknownVerb' "$SESSION/file.txt" || compose_fail "the plugin's refusal did not arrive as the envelope's e.code" "$SESSION/file.txt"
echo "ok: a --plugin run answers a script's verb call with the plugin's envelope"

echo "── and through --repl, so a .run of that session reaches it too"
# THE PATH THAT GOES THROUGH STDIN IS NOT CONVERTED, unlike an argument: the shell rewrites an argument for a Windows
# program, so `--plugin <posix path>` works, while a `.run /d/...` LINE arrives at the engine verbatim and resolves
# nowhere (measured: "No files matched: /d/Reasonix/.../probe.slc"). `cygpath -m` gives a Windows path with forward
# slashes, which both the engine and this file handle.
PROBE_RUN="$(cygpath -m "$SESSION/probe.slc" 2>/dev/null || printf '%s' "$SESSION/probe.slc")"
printf '.run %s\n.exit\n' "$PROBE_RUN" | "$ENGINE" --plugin "$SESSION/JitPluginFixture.dll" JitPluginFixture.Entry --repl > "$SESSION/repl.txt" 2>&1
grep -qF 'ok=True r=42' "$SESSION/repl.txt" || compose_fail "a .run in a --plugin session did not reach the plugin" "$SESSION/repl.txt"
echo "ok: the session's interpreter keeps the door for every .run"

echo "── a plugin that is not there is refused by name, before the script runs"
"$ENGINE" --plugin "$SESSION/nope.dll" JitPluginFixture.Entry "$SESSION/probe.slc" > "$SESSION/missing.txt" 2>&1
rc=$?
[ "$rc" -eq 2 ] || compose_fail "a missing plugin exited $rc, expected 2 (usage)" "$SESSION/missing.txt"
grep -qF 'the plugin assembly was not found' "$SESSION/missing.txt" || compose_fail "the refusal does not name the assembly that is missing" "$SESSION/missing.txt"
grep -qF 'hosted=' "$SESSION/missing.txt" && compose_fail "the script ran although the plugin could not be loaded" "$SESSION/missing.txt"
echo "ok: a missing plugin is one sentence naming the file, and nothing runs"

echo "── a wrong entry TYPE names the HRESULT that says why"
"$ENGINE" --plugin "$SESSION/JitPluginFixture.dll" Nope.Missing "$SESSION/probe.slc" > "$SESSION/type.txt" 2>&1
grep -qF '0x80131522' "$SESSION/type.txt" || compose_fail "a wrong entry type did not name 0x80131522" "$SESSION/type.txt"
echo "ok: a wrong entry type is refused with the HRESULT and the qualified name it tried"

# ── THE SCRIPT'S OWN DOOR: `Bridge.LoadJitPlugin` ───────────────────────────────────────────────────────────────────
# The prefix opens the plugin from the HOST; `Bridge.LoadJitPlugin` opens the same thing from the SCRIPT, through the same
# loader. This half needs no `--plugin` on the command line at all, which is the property being pinned, and its path
# spelling is `./`, i.e. beside the subject (the engine's ONE path rule), not the process directory.
cat > "$SESSION/loadprobe.slc" <<'SLC'
print("before: hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
// ONE ARGUMENT: the entry type is found in the DLL, as the one public static [UnmanagedCallersOnly] PluginInit.
Bridge.LoadJitPlugin("./JitPluginFixture.dll");
print("after: hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
var answer = Bridge.CallEnvelope("add", { a: 20, b: 22 });
print("loaded: ok=" + answer.Ok + " r=" + answer.Value);
if (answer.Value != 42) { exit(1); }
// THE VERB FORM: the engine writes v, m and p and returns r, so the script names a verb and its parameters.
var sumVerb = Bridge.Call("add", { a: 20, b: 22 });
print("verb-add=" + sumVerb);
if (sumVerb != 42) { exit(5); }
var echoVerb = Bridge.Call("echo", { a: 7, b: 9 });
print("verb-echo=" + echoVerb);
if ("echo:7/9" != echoVerb) { exit(6); }
// THE ENVELOPE FORM: CallEnvelope answers the typed BridgeEnvelope instead of throwing, and a refusal is read from
// its own members. The TYPE is asserted too, because a script names it (`typeof`, `is`) like any other engine type.
var refusedEnvelope = Bridge.CallEnvelope("nope", {});
print("verb-envelope=" + refusedEnvelope.Ok + " code=" + refusedEnvelope.Error.Code);
if (refusedEnvelope.Ok || refusedEnvelope.Error.Code != "UnknownVerb") { exit(7); }
if (typeof(refusedEnvelope) != "BridgeEnvelope") { exit(9); }
if (refusedEnvelope is not BridgeEnvelope) { exit(10); }
// ...and the parsed document stays reachable for a member the contract does not name
if ("" + answer.Document["ok"] != "True" || answer.Error != null) { exit(11); }
var caughtCode = "";
try { Bridge.Call("nope", {}); }
catch (var ex) { caughtCode = "" + ex.Value["code"]; }
print("verb-caught=" + caughtCode);
if (caughtCode != "UnknownVerb") { exit(8); }
// the SAME plugin asked for twice, the second time with its type STATED: one plugin, not two, because the
// comparison is made in the loader's qualified form, and a runtime cannot be unloaded
var same = Bridge.LoadJitPlugin("./JitPluginFixture.dll", "JitPluginFixture.Entry");
print("same-again=" + same);
// a DIFFERENT plugin while one is loaded is an ERROR, because a second runtime would outlive the first
var other = "";
try { Bridge.LoadJitPlugin("./nope.dll", "X.Entry"); }
catch (var ex) { other = ex.Message; }
print("other=[" + other + "]");
if (!other.Contains("already carries a plugin door")) { exit(2); }
// dropping the door is the half a script can have: it is NOT an unload, and the sample and the page say so
Bridge.DetachPlugin();
print("detached: hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
if (Bridge.Hosted || Bridge.Plugin) { exit(3); }
// ...and a detached session may route to another plugin, which is what the detach is FOR
Bridge.LoadJitPlugin("./JitPluginFixture.dll");
var second = Bridge.Call("echo", { a: 7, b: 9 });
print("reloaded: " + second);
if ("echo:7/9" != second) { exit(4); }
SLC

echo "── a script opens the plugin itself, with no --plugin on the command line"
"$ENGINE" "$SESSION/loadprobe.slc" > "$SESSION/load.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "Bridge.LoadJitPlugin exited $rc (its own exit(1..11) names the step that failed)" "$SESSION/load.txt"
grep -qF 'before: hosted=False plugin=False' "$SESSION/load.txt" || compose_fail "the script was already hosted before it loaded anything" "$SESSION/load.txt"
grep -qF 'after: hosted=True plugin=True' "$SESSION/load.txt" || compose_fail "Bridge.LoadJitPlugin did not install a door" "$SESSION/load.txt"
grep -qF 'loaded: ok=True r=42' "$SESSION/load.txt" || compose_fail "the script-opened plugin did not answer its verb" "$SESSION/load.txt"
grep -qF 'verb-add=42' "$SESSION/load.txt" || compose_fail "Bridge.Call(verb, params) did not return the plugin's r" "$SESSION/load.txt"
grep -qF 'verb-echo=echo:7/9' "$SESSION/load.txt" || compose_fail "Bridge.Call(verb, params) did not carry the parameters" "$SESSION/load.txt"
grep -qF 'verb-envelope=False code=UnknownVerb' "$SESSION/load.txt" || compose_fail "Bridge.CallEnvelope did not answer the host's envelope" "$SESSION/load.txt"
grep -qF 'verb-caught=UnknownVerb' "$SESSION/load.txt" || compose_fail "Bridge.Call(verb, params) did not throw the host's code" "$SESSION/load.txt"
grep -qF 'is already loaded in this session, so nothing was started' "$SESSION/load.txt" || compose_fail "a second load of the SAME plugin did not warn (it must not start a second runtime)" "$SESSION/load.txt"
grep -qF 'same-again=True' "$SESSION/load.txt" || compose_fail "a second load of the same plugin did not answer true" "$SESSION/load.txt"
grep -qF 'already carries a plugin door' "$SESSION/load.txt" || compose_fail "a DIFFERENT plugin was not refused by name" "$SESSION/load.txt"
grep -qF 'detached: hosted=False plugin=False' "$SESSION/load.txt" || compose_fail "Bridge.DetachPlugin did not drop the door" "$SESSION/load.txt"
grep -qF 'reloaded: echo:7/9' "$SESSION/load.txt" || compose_fail "a detached session could not route to another plugin" "$SESSION/load.txt"
echo "ok: LoadJitPlugin discovers the entry type, repeats itself by name, refuses a different one, and a detach lets it in"

# A HOST'S DOOR IS NOT THE SCRIPT'S TO REPLACE, which is the only thing the two installers are told apart for.
cat > "$SESSION/hostedprobe.slc" <<'SLC'
print("hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
var note = "";
try { Bridge.LoadJitPlugin("./JitPluginFixture.dll", "JitPluginFixture.Entry"); }
catch (var ex) { note = ex.Message; }
print("refused=[" + note + "]");
if (!note.Contains("a HOST installed this session's bridge")) { exit(1); }
var answer = Bridge.Call("add", { a: 20, b: 22 });
print("still=" + answer);
if (answer != 42) { exit(2); }
if (Bridge.Plugin) { exit(3); }
SLC

echo "── a host's door is reported as the host's, and a refused load leaves it working"
"$ENGINE" --plugin "$SESSION/JitPluginFixture.dll" JitPluginFixture.Entry "$SESSION/hostedprobe.slc" > "$SESSION/hosted.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "the host-door probe exited $rc" "$SESSION/hosted.txt"
grep -qF 'hosted=True plugin=False' "$SESSION/hosted.txt" || compose_fail "a --plugin door did not report itself as the host's" "$SESSION/hosted.txt"
grep -qF "a HOST installed this session's bridge" "$SESSION/hosted.txt" || compose_fail "LoadJitPlugin did not refuse a host's bridge by name" "$SESSION/hosted.txt"
grep -qF 'still=42' "$SESSION/hosted.txt" || compose_fail "the refused load broke the door that was already there" "$SESSION/hosted.txt"
echo "ok: Bridge.Plugin answers False for a host's door, and the refusal changes nothing"

# A PLUGIN THAT IS NOT THERE is a CATCHABLE script error through this door (the prefix exits 2 before anything runs).
cat > "$SESSION/missingprobe.slc" <<'SLC'
var note = "";
try { Bridge.LoadJitPlugin("./nope.dll"); }
catch (var ex) { note = ex.Message; }
print("missing=[" + note + "]");
if (!note.Contains("the plugin assembly was not found")) { exit(1); }
var stated = "";
try { Bridge.LoadJitPlugin("./nope.dll", "X.Entry"); }
catch (var ex) { stated = ex.Message; }
print("missing-stated=[" + stated + "]");
if (!stated.Contains("the plugin assembly was not found")) { exit(2); }
if (Bridge.Hosted) { exit(3); }
SLC

echo "── a missing plugin is a catchable script error, and it installs nothing"
"$ENGINE" "$SESSION/missingprobe.slc" > "$SESSION/loadmiss.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "the missing-plugin probe exited $rc (expected the script to catch the refusal)" "$SESSION/loadmiss.txt"
grep -qF 'the plugin assembly was not found' "$SESSION/loadmiss.txt" || compose_fail "the refusal does not name the assembly that is missing" "$SESSION/loadmiss.txt"
grep -qF 'missing-stated=[' "$SESSION/loadmiss.txt" || compose_fail "the stated-type probe never ran" "$SESSION/loadmiss.txt"
echo "ok: Bridge.LoadJitPlugin refuses a missing assembly by name, and the script can catch it"

# ── THE NATIVE LEG: the SAME contract, a DLL that is not managed at all ─────────────────────────────────────────
# Nothing above proves a plugin written in C can arrive: those files are a managed assembly plus its runtimeconfig,
# and the loader starts a CoreCLR for them. This one is compiled from `src/jitplugins/native/jitplugin_native.c` by
# GCC x64 into a plain DLL that EXPORTS `PluginInit`, so the host loads it with LoadLibrary and no second runtime is
# started (which is why no nethost.dll is needed here: it is only the bridge to hostfxr).
echo "── building the native plugin fixture (GCC x64)"
NATIVE="$OUT/native"
mkdir -p "$NATIVE"
bash "$ROOT/src/jitplugins/native/build.sh" "$NATIVE/JitPluginNative.dll" || { echo "FAIL: the native fixture did not build" >&2; exit 1; }

echo "── the native plugin through the HOST's door (--plugin), the same probe as the managed one"
"$ENGINE" --plugin "$NATIVE/JitPluginNative.dll" native "$SESSION/probe.slc" > "$SESSION/native-host.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "the native plugin answered the probe with exit $rc" "$SESSION/native-host.txt"
grep -qF 'hosted=True' "$SESSION/native-host.txt" || compose_fail "the native plugin did not install a door" "$SESSION/native-host.txt"
grep -qF 'ok=True r=42' "$SESSION/native-host.txt" || compose_fail "the native plugin did not answer add" "$SESSION/native-host.txt"
grep -qF 'bad=False code=UnknownVerb' "$SESSION/native-host.txt" || compose_fail "the native plugin did not refuse an unknown verb by code" "$SESSION/native-host.txt"
echo "ok: a C plugin answers the same probe as the managed one, through --plugin"

echo "── the native plugin through the SCRIPT's door (Bridge.LoadNativePlugin), with no command line at all"
cat > "$SESSION/nativeload.slc" <<'SLC'
print("before: hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
// THE WRONG MEMBER, asked on purpose: a native DLL has no type to discover and no runtimeconfig beside it, so the
// managed member refuses it BY NAME and points at LoadNativePlugin.
var wrong = "";
try { Bridge.LoadJitPlugin("./JitPluginNative.dll"); }
catch (var ex) { wrong = ex.Message; }
print("wrong-member=[" + wrong + "]");
if (!wrong.Contains("is a NATIVE plugin")) { exit(1); }
Bridge.LoadNativePlugin("./JitPluginNative.dll");
print("after: hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
var answer = Bridge.Call("add", { a: 20, b: 22 });
print("add=" + answer);
if (answer != 42) { exit(2); }
// a non-ASCII answer and a 4000-byte one, so the native leg covers the length convention and UTF-8 rather than only add
var utf8 = Bridge.Call("utf8", {});
print("utf8=" + utf8);
var big = Bridge.Call("long", { n: 4000 });
print("long=" + big.Length);
if (big.Length != 4000) { exit(3); }
SLC
# The native probe lives BESIDE the DLL, because its path is `./`, which the engine resolves beside the script.
cp "$SESSION/nativeload.slc" "$NATIVE/nativeload.slc"
"$ENGINE" "$NATIVE/nativeload.slc" > "$SESSION/native-load.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "Bridge.LoadNativePlugin on a native plugin exited $rc" "$SESSION/native-load.txt"
grep -qF 'before: hosted=False plugin=False' "$SESSION/native-load.txt" || compose_fail "the native probe was already hosted" "$SESSION/native-load.txt"
grep -qF 'after: hosted=True plugin=True' "$SESSION/native-load.txt" || compose_fail "Bridge.LoadNativePlugin did not open the native DLL" "$SESSION/native-load.txt"
grep -qF 'is a NATIVE plugin' "$SESSION/native-load.txt" || compose_fail "LoadJitPlugin did not refuse the native DLL by name" "$SESSION/native-load.txt"
grep -qF 'add=42' "$SESSION/native-load.txt" || compose_fail "the script-opened native plugin did not answer add" "$SESSION/native-load.txt"
grep -qF 'utf8=café et 👋' "$SESSION/native-load.txt" || compose_fail "the native plugin's UTF-8 answer did not survive the door" "$SESSION/native-load.txt"
grep -qF 'long=4000' "$SESSION/native-load.txt" || compose_fail "the native plugin's probe-then-read length convention did not hold" "$SESSION/native-load.txt"
echo "ok: a script opens the C plugin itself, and its UTF-8 and 4000-byte answers arrive intact"

# ── AND IT IS GIVEN BACK: a native module is RELEASED by the detach, and a script cannot unmap a live door ────────
# The legs above prove the door WORKS. These two prove what the door costs and what it takes with it, and both were
# wrong until this pin existed:
#   1. `Bridge.DetachPlugin` on a NATIVE plugin used to drop the delegate only, so the image stayed in the process for
#      good (measured: `File.Delete` on the plugin's own file answered "locked" after the detach). The loader threw the
#      module handle away, and the "nothing unloads it" sentence was inherited from the MANAGED route, where it is
#      true (`hostfxr_close` leaves coreclr loaded). A plain DLL is not that case: `FreeLibrary` really does unmap it.
#   2. A script holding the plugin's own path could take the reference the LOADER held: `LoadLibrary(path)` answers the
#      module that is already there, and `FreeLibrary` then dropped the loader's reference, so the next `Bridge.Call`
#      ran into unmapped memory (Debug: 0xC0000005 inside `LoadedJitPlugin.Dispatch`; AOT: SIGSEGV, rc 139). Both the
#      library form and the raw-handle form are refused BY NAME now, and the door that was protected still answers.
# TWO FILES, because the first must detach with nothing else holding the module and the second must NOT detach: a
# script's own reference would survive the release and read as "the module is still locked", blaming the wrong thing.
NATIVE_WIN="$(cygpath -m "$NATIVE" 2>/dev/null || printf '%s' "$NATIVE")"
cat > "$NATIVE/releaseprobe.slc" <<SLC
string TryDelete(string p) { try { File.Delete(p); return "DELETED"; } catch (var e) { return "locked"; } }
var path = "$NATIVE_WIN/Releasable.dll";
File.Copy("$NATIVE_WIN/JitPluginNative.dll", path, true);
Bridge.LoadNativePlugin(path);
var answer = Bridge.Call("add", { a: 20, b: 22 });
print("add=" + answer);
if (answer != 42) { exit(1); }
Bridge.DetachPlugin();
print("detached: hosted=" + Bridge.Hosted + " plugin=" + Bridge.Plugin);
if (Bridge.Hosted || Bridge.Plugin) { exit(2); }
print("deletable=" + TryDelete(path));
SLC

echo "── a native plugin's module is released by the detach, so its file can go"
"$ENGINE" "$NATIVE/releaseprobe.slc" > "$SESSION/native-release.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "the release probe exited $rc (its own exit names the step that failed)" "$SESSION/native-release.txt"
grep -qF 'add=42' "$SESSION/native-release.txt" || compose_fail "the native plugin did not answer before the detach" "$SESSION/native-release.txt"
grep -qF 'detached: hosted=False plugin=False' "$SESSION/native-release.txt" || compose_fail "Bridge.DetachPlugin did not drop the native door" "$SESSION/native-release.txt"
grep -qF 'deletable=DELETED' "$SESSION/native-release.txt" || compose_fail "the module stayed mapped after the detach, so the file could not be deleted (a native plugin's module must be released)" "$SESSION/native-release.txt"
echo "ok: Bridge.DetachPlugin releases a native plugin's module, and the file deletes afterwards"

cat > "$NATIVE/guardprobe.slc" <<SLC
string TryDelete(string p) { try { File.Delete(p); return "DELETED"; } catch (var e) { return "locked"; } }
var path = "$NATIVE_WIN/Guarded.dll";
File.Copy("$NATIVE_WIN/JitPluginNative.dll", path, true);
Bridge.LoadNativePlugin(path);
var first = Bridge.Call("add", { a: 20, b: 22 });
print("add=" + first);
if (first != 42) { exit(1); }
// THE SCRIPT'S OWN LOAD AND FREE of the file the door calls through: refused, and the refusal says what to do
var lib = LoadLibrary(path);
var refused = "";
try { lib.Free(); } catch (var ex) { refused = ex.Message; }
print("free-refused=[" + refused + "]");
if (!refused.Contains("Bridge.DetachPlugin")) { exit(2); }
// THE SAME UNMAP BY ANOTHER SPELLING: the handle, not the library object
var again = LoadLibrary(path);
var refusedHandle = "";
try { FreeLibrary(again.Handle); } catch (var ex) { refusedHandle = ex.Message; }
print("handle-refused=[" + refusedHandle + "]");
if (!refusedHandle.Contains("Bridge.DetachPlugin")) { exit(3); }
// ...and the door those refusals protected still answers, which is the whole point of refusing
var still = Bridge.Call("add", { a: 1, b: 2 });
print("still=" + still);
if (still != 3) { exit(4); }
// THE OTHER SIDE OF THE SAME RULE: the detach releases the module, so the two handles this script still holds name
// nothing afterwards. Their free must release NOTHING rather than ask the loader a second time (that second
// FreeLibrary answered with an exception on the AOT build, which surfaced as a warning at the end of every run).
Bridge.DetachPlugin();
var gone = TryDelete(path);
print("deletable=" + gone);
if (gone != "DELETED") { exit(5); }
lib.Free();
FreeLibrary(again.Handle);
print("after-free: no throw");
SLC

echo "── a script cannot free the module a live plugin door calls through"
"$ENGINE" "$NATIVE/guardprobe.slc" > "$SESSION/native-guard.txt" 2>&1
rc=$?
[ "$rc" -eq 0 ] || compose_fail "the free-guard probe exited $rc (its own exit names the step that failed)" "$SESSION/native-guard.txt"
grep -qF 'add=42' "$SESSION/native-guard.txt" || compose_fail "the guarded probe's plugin did not answer first" "$SESSION/native-guard.txt"
grep -qF 'free-refused=[' "$SESSION/native-guard.txt" || compose_fail "the free-guard probe never reported a refusal" "$SESSION/native-guard.txt"
grep -qF 'handle-refused=[' "$SESSION/native-guard.txt" || compose_fail "the raw-handle probe never reported a refusal" "$SESSION/native-guard.txt"
grep -qF 'still=3' "$SESSION/native-guard.txt" || compose_fail "the door stopped answering after the refused free" "$SESSION/native-guard.txt"
grep -qF 'deletable=DELETED' "$SESSION/native-guard.txt" || compose_fail "the module was still mapped after the detach in the guarded probe" "$SESSION/native-guard.txt"
grep -qF 'after-free: no throw' "$SESSION/native-guard.txt" || compose_fail "freeing a handle the engine had already released threw" "$SESSION/native-guard.txt"
grep -qF 'Bridge.DetachPlugin' "$SESSION/native-guard.txt" || compose_fail "the refusal does not name Bridge.DetachPlugin as the way to release the module" "$SESSION/native-guard.txt"
echo "ok: freeing a doored module is refused by name, both as a library and as a raw handle, and the door survives"

echo "── 11 composition check(s) green"
exit 0
