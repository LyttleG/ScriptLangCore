#!/usr/bin/env bash
# src/build/embed-check.sh, drive the published engine DLL through its C ABI, the way a native host would.
#
# WHY THIS IS NOT A SUITE: the suites run INSIDE the engine, so nothing a suite can do loads the engine's own DLL.
# This instrument is the other side of the boundary: a separate host process (`src/embed/`, a net11.0 console project
# that references nothing from the engine) that loads the artifact and calls the exports, so the ABI is verified
# against the SHIPPED binary rather than against the source that produced it.
#
# USAGE
#   bash src/build/embed-check.sh [engine-dll] [engine-exe]
# Defaults: publish-dll/ScriptLangCore.dll and publish-aot/ScriptLangCore.exe (`compile_aot.bat` builds both).
#
# THE RULE THIS SHARES WITH THE GATE: an artifact older than src/ does not describe this tree, so it is REFUSED rather
# than measured (the stale-binary trap has produced wrong answers more than once in this repository).
#
# EXIT CODES: 0 every check passed, 1 a check failed (the host says which), 2 refused to start (no DLL, a stale DLL, a
# build failure, a missing dotnet).
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

# THE ONE RULE for "does this artifact describe this tree", shared with the gate, bundle-parity.sh and ab-check.sh:
# one source set (the engine's, so `src/embed` and any `obj`/`bin` a nested project writes INSIDE src are pruned) and
# one wording. Restated here it was a second rule, and a second rule is how a stale artifact gets past one instrument.
. "$ROOT/src/build/_artifacts.sh"

DLL="${1:-publish-dll/ScriptLangCore.dll}"
EXE="${2:-publish-aot/ScriptLangCore.exe}"

if [ ! -f "$DLL" ]; then
  echo "no engine DLL at '$DLL'" >&2
  echo "build it first: $SLC_REMEDY_DLL" >&2
  exit 2
fi

if slc_refuse_stale "$DLL" "the engine DLL" "$SLC_REMEDY_DLL" \
     "the host and the launcher are SEPARATE programs, so their sources are pruned from the comparison" "$ROOT"; then
  echo "FAIL: this artifact would answer for a tree it does not contain" >&2
  exit 2
fi

ARGS=("$DLL")
if [ -f "$EXE" ]; then
  ARGS+=("$EXE")
else
  echo "note: no '$EXE', so the bundle section will compile its subject with the engine exe it cannot find" >&2
fi

echo "engine:  $DLL"
[ -f "$EXE" ] && echo "compiler: $EXE"

dotnet run --project src/embed/EmbedCheck.csproj -c Release -- "${ARGS[@]}"
status=$?

# THE PACKAGED LEG: a script carried in the launcher's own resource must run as an application. This is the only check
# of the packaging chain (pack -> Win32 resource -> content detection -> run), and it needs all three artifacts: the
# launcher to copy, the engine EXE to drive `slc-pack.slc`, and the DLL beside the copy.
LAUNCHER="$(dirname "$DLL")/ScriptLangCore.Launcher.exe"
[ -f "$LAUNCHER" ] || LAUNCHER="publish-launcher/ScriptLangCore.Launcher.exe"

# THE LAUNCHER LEGS LOAD THE DLL BESIDE THE LAUNCHER, not the one the ABI leg just tested, so the copy is checked HERE,
# before any leg runs: `compile_aot.bat` stages it, and a republished `publish-dll` alone leaves these legs measuring an
# OLDER engine (found by injection: a disabled verb failed in the ABI leg and passed here). A NOTE was not enough, and
# that cost a round: a fix that was real in `publish-dll` looked inert through the launcher, whose suite CHILD loads the
# staged copy too (reported here as "factory_callback_test is failing while using the launcher"). A publish of the
# LAUNCHER project alone can also DELETE the staged DLL, since it is not a file that project produces, which is the
# second way this copy goes missing. Both are refused by the ONE rule, with the restage command.
STAGED="$(dirname "$LAUNCHER")/ScriptLangCore.dll"
if ! slc_refuse_copy_drift "$DLL" "$STAGED" "the DLL beside the launcher" "copy $DLL $STAGED   (or run compile_aot.bat)"; then
  if [ ! -f "$STAGED" ]; then
    echo "REFUSING: no engine DLL beside the launcher ($STAGED), so the legs below cannot run one." >&2
    echo "  stage it: copy $DLL $STAGED   (or run compile_aot.bat, which stages it as its last step)" >&2
    exit 2
  fi
else
  echo "FAIL: the launcher legs would have measured a DIFFERENT engine, so nothing below describes this tree" >&2
  exit 2
fi
PACKER="assets/language/probes/slc-pack.slc"
LAUNCHER_REL="publish-launcher/ScriptLangCore.Launcher.exe"   # the repository-relative spelling a session types

if [ -f "$LAUNCHER" ] && [ -f "$EXE" ] && [ -f "$PACKER" ]; then
  PACKDIR="$(cd "$(dirname "$LAUNCHER")" && pwd)"
  SUBJECT="$(mktemp -d)/packaged.slc"
  cat > "$SUBJECT" <<'SLC'
print("packaged app ran");
print("args=" + Application.Args.Count);
print("dir has assets: " + Directory.Exists(Path.Combine(Script.Directory(), "assets")));
SLC
  PACKED="$PACKDIR/packcheck.exe"
  rm -f "$PACKED"
  REPORT="$("$EXE" "$PACKER" "$LAUNCHER" "$SUBJECT" "$PACKED" 2>&1)"
  if [ ! -f "$PACKED" ]; then
    echo "FAIL: slc-pack did not produce '$PACKED'" >&2
    echo "$REPORT" >&2
    status=1
  else
    OUTBOX="$(mktemp)"; "$PACKED" one two > "$OUTBOX" 2>&1; packedStatus=$?
    if [ "$packedStatus" -eq 0 ] && grep -q "packaged app ran" "$OUTBOX" && grep -q "args=2" "$OUTBOX" \
       && grep -q "dir has assets: True" "$OUTBOX"; then
      echo "ok: a packaged exe ran the script it carries ($(wc -c < "$PACKED" | tr -d ' ') bytes, exit 0, ./ resolves beside it)"
    else
      echo "FAIL: the packaged exe answered $packedStatus" >&2
      cat "$OUTBOX" >&2
      status=1
    fi

    # THE LAUNCHER SAYS NOTHING ABOUT A RUN'S VERDICT BY DEFAULT, because a script that CHOOSES its exit code is not
    # failing: measured, a suite whose child ends `exit(3)` asserts that child's stderr is silent, and the two JSON lines
    # the launcher used to write there made `vm_exit_parity` read `"Error":false` as an error report and made
    # `single_instance_test` count three lines where it expects one. The exit code IS the verdict, exactly as on the CLI,
    # and `SLC_LAUNCHER_INFO=1` is the door for a person who wants the JSON.
    EXITSUB="$(mktemp -d)/exitcode.slc"
    printf 'print("chose 3");\nexit(3);\n' > "$EXITSUB"
    OUTBOX="$(mktemp)"; ERRBOX="$(mktemp)"
    "$LAUNCHER" "$EXITSUB" > "$OUTBOX" 2> "$ERRBOX"; choseStatus=$?
    if [ "$choseStatus" -eq 3 ] && grep -q "chose 3" "$OUTBOX" && [ ! -s "$ERRBOX" ]; then
      SLC_LAUNCHER_INFO=1 "$LAUNCHER" "$EXITSUB" > /dev/null 2> "$ERRBOX"; infoStatus=$?
      if [ "$infoStatus" -eq 3 ] && grep -q "the run answered 3" "$ERRBOX" && grep -q '"Status":3' "$ERRBOX"; then
        echo "ok: a run that chooses exit 3 leaves stderr empty, and SLC_LAUNCHER_INFO=1 asks for the verdict"
      else
        echo "FAIL: SLC_LAUNCHER_INFO=1 did not report the verdict (exit $infoStatus)" >&2
        cat "$ERRBOX" >&2
        status=1
      fi
    else
      echo "FAIL: the launcher wrote to stderr for a run that chose exit 3 (exit $choseStatus)" >&2
      cat "$ERRBOX" >&2
      status=1
    fi
    rm -f "$OUTBOX" "$ERRBOX"

    # REPL, while a flag the app invents still reaches the app. Both halves are the rule, and each fails on its own.
    "$PACKED" --verbose alpha > "$OUTBOX" 2>&1
    if [ $? -eq 0 ] && grep -q "packaged app ran" "$OUTBOX"; then
      echo "ok: an argument the engine does not own reaches the packaged app"
    else
      echo "FAIL: a packaged exe did not run its payload for its own flag" >&2
      cat "$OUTBOX" >&2
      status=1
    fi

    PACKOUT="$PACKDIR/packcheck_out.slcbc"
    rm -f "$PACKOUT"
    "$PACKED" --compile "$SUBJECT" "$PACKOUT" --source=strip > "$OUTBOX" 2>&1
    if [ $? -eq 0 ] && [ -f "$PACKOUT" ]; then
      echo "ok: an argument the ENGINE owns takes over in a packaged exe (--compile wrote its bundle)"
    else
      echo "FAIL: the packaged exe did not let --compile take over" >&2
      cat "$OUTBOX" >&2
      status=1
    fi
    rm -f "$OUTBOX" "$PACKED" "$PACKOUT"
  fi
rm -rf "$(dirname "$SUBJECT")"
  OUTBOX="$(mktemp)"   # the leg above deletes its own box, and `set -u` is on

  # ── THE TWO ARMS NOTHING HAD RUN THROUGH THE LAUNCHER ────────────────────────────────────────────────────────
  # `--compile` and the REPL were measured; `--dump-bytecode` and `--mcp` were only ever run through the exe, and both
  # go through `slc_main`'s dispatch exactly as a host would call it.
  DUMPSUB="$(mktemp -d)/dump.slc"
  printf 'int Twice(int n) { return n * 2; }\nprint(Twice(21));\n' > "$DUMPSUB"
  "$LAUNCHER" --dump-bytecode "$DUMPSUB" > "$OUTBOX" 2>&1
  if [ $? -eq 0 ] && grep -q "^== Twice" "$OUTBOX"; then
    echo "ok: --dump-bytecode runs through the launcher (a body was dumped)"
  else
    echo "FAIL: --dump-bytecode did not answer through the launcher" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  rm -rf "$(dirname "$DUMPSUB")"

  # `--mcp` BLOCKS, so it is started, asked once, and stopped: the assertion is that the LISTENER came up through the
  # launcher's session, which is the whole point of the arm being inherited.
  PORT=$(( 9100 + ($$ % 300) ))
  "$LAUNCHER" --mcp --port "$PORT" --repl > "$OUTBOX" 2>&1 &
  MCPPID=$!
  MCPCODE=""
  for _ in $(seq 1 40); do
    sleep 0.25
    MCPCODE="$(curl -s -o /dev/null -w '%{http_code}' --max-time 1 "http://localhost:$PORT/" 2>/dev/null)"
    [ "$MCPCODE" != "000" ] && [ -n "$MCPCODE" ] && break
  done
  kill "$MCPPID" 2>/dev/null; wait "$MCPPID" 2>/dev/null
  if grep -q "SSE server on" "$OUTBOX" && [ "$MCPCODE" != "000" ] && [ -n "$MCPCODE" ]; then
    echo "ok: --mcp starts the server through the launcher (answered HTTP $MCPCODE on port $PORT)"
  else
    echo "FAIL: --mcp did not come up through the launcher (answered '$MCPCODE')" >&2
    cat "$OUTBOX" >&2
    status=1
  fi

  # ── A PACKAGED BUNDLE, WHICH IS THE SHAPE A SOURCE-LESS APP TAKES ────────────────────────────────────────────
  BSUB="$(mktemp -d)/bundled.slc"
  cat > "$BSUB" <<'SLC'
int Twice(int n) { return n * 2; }
print("bundled app ran, twice(21)=" + Twice(21));
SLC
  BBUNDLE="$PACKDIR/packcheck.slcbc"
  "$EXE" --compile "$BSUB" "$BBUNDLE" --source=strip > "$OUTBOX" 2>&1
  if [ -f "$BBUNDLE" ]; then
    BPACKED="$PACKDIR/packcheck-bundle.exe"
    rm -f "$BPACKED"
    "$EXE" "$PACKER" "$LAUNCHER" "$BBUNDLE" "$BPACKED" > "$OUTBOX" 2>&1
    if [ -f "$BPACKED" ] && "$BPACKED" > "$OUTBOX" 2>&1 && grep -q "bundled app ran, twice(21)=42" "$OUTBOX"; then
      echo "ok: a packaged SOURCE-LESS bundle runs (slc_run_bundle_bytes, no file to name)"
    else
      echo "FAIL: a packaged bundle did not run" >&2
      cat "$OUTBOX" >&2
      status=1
    fi
    rm -f "$BPACKED" "$BBUNDLE"
  else
    echo "FAIL: the engine did not compile the bundle subject" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  rm -rf "$(dirname "$BSUB")"

  # ── A REPO-RELATIVE INPUT IS FOUND, which is what a session naturally types (reported: "no launcher at
  # 'publish-launcher\ScriptLangCore.Launcher.exe'" from a REPL started in that very folder). The path search is the one
  # an import follows, and the report names what it USED, so a reader can see which file was packed.
  RSUB="$(mktemp -d)/relative.slc"
  printf 'print("relative path app ran");\n' > "$RSUB"
  ROUT="$PACKDIR/packcheck-relative.exe"   # beside the DLL, so the copy can run
  EXEABS="$(cd "$(dirname "$EXE")" && pwd)/$(basename "$EXE")"
  # THE REPORT'S WORDING IS NOT THE CONTRACT: it was reworded once already (lowercase "resolved: launcher" became
  # "Resolved:  launcher" inside the packer's boxed summary), and that alone turned this leg red while the packer still
  # resolved the file and named it. The grep below is case- and spacing-tolerant for that reason; what the leg really
  # asserts is that the copy was written AND runs its payload.
  ( cd "$PACKDIR" && "$EXEABS" "$ROOT/$PACKER" "$LAUNCHER_REL" "$RSUB" "$ROUT" ) > "$OUTBOX" 2>&1
  packStatus=$?
  if [ "$packStatus" -eq 0 ] && [ -f "$ROUT" ] && grep -qiE 'resolved:[[:space:]]+launcher' "$OUTBOX" \
     && ( cd "$PACKDIR" && "$ROUT" ) > "$OUTBOX" 2>&1 && grep -q "relative path app ran" "$OUTBOX"; then
    echo "ok: a repo-relative launcher path resolves by search, and the report names the file it used"
  else
    echo "FAIL: a repo-relative launcher path was not resolved" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  rm -f "$ROUT"; rm -rf "$(dirname "$RSUB")"
  # ── AN APP THAT NAMES ITSELF: a complete version resource, and --icon replaces the launcher's own icon ────────────
  VSUB="$(mktemp -d)/versioned.slc"
  printf 'print("versioned app ran");\n' > "$VSUB"
  VPACKED="$PACKDIR/packcheck-versioned.exe"
  rm -f "$VPACKED"
  "$EXE" "$PACKER" "$LAUNCHER" "$VSUB" "$VPACKED" --version 1.2.3.4 > "$OUTBOX" 2>&1
  packStatus=$?
  # THE STAMP IS READ FROM THE ARTIFACT, because that is the question ("did the app get a version"): PowerShell's
  # FileVersionInfo walks the resource tree the packer wrote, and it does not care how the packer WORDED its report (that
  # wording was reworded once already and turned this leg red while the stamp was there). Where PowerShell is absent the
  # leg falls back to the report's own line, which is case- and spacing-tolerant for the same reason.
  STAMPED=""
  NAMED=""
  if command -v powershell >/dev/null 2>&1 && [ -f "$VPACKED" ]; then
    VPACKED_WIN="$(cygpath -w "$VPACKED")"
    # THREE PROPERTIES, because the resource carries three kinds of fact and each was wrong once: FileVersionRaw is the
    # four numbers (VS_FIXEDFILEINFO), FileVersion is the SAME numbers as a STRING (StringFileInfo, which a numbers-only
    # blob left empty), and ProductName is the app's own name (which a copy of the launcher used to report as
    # "ScriptLangCore"). Reading them together is what makes this leg about a well-formed tree rather than about one field.
    STAMPED="$(powershell -NoProfile -Command "\$v=(Get-Item '$VPACKED_WIN').VersionInfo; \$v.FileVersionRaw.ToString() + '|' + \$v.FileVersion + '|' + \$v.ProductName + '|' + \$v.FileDescription" 2>/dev/null | tr -d '\r\n')"
    NAMED="$(printf '%s' "$STAMPED" | cut -d'|' -f2-4)"
  fi
  stampedOk=0
  if [ -n "$STAMPED" ]; then
    if [ "${STAMPED%%|*}" = "1.2.3.4" ]; then stampedOk=1; fi
  elif grep -qiE 'version:[[:space:]]+1\.2\.3\.4' "$OUTBOX"; then
    stampedOk=1
  fi
  if [ "$packStatus" -eq 0 ] && [ -f "$VPACKED" ] && [ "$stampedOk" -eq 1 ] && "$VPACKED" > "$OUTBOX" 2>&1 \
     && grep -q "versioned app ran" "$OUTBOX"; then
    echo "ok: --version stamps the app and it still runs (reads $STAMPED from the artifact)"
  else
    echo "FAIL: --version did not stamp the app" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  # THE RESOURCE NAMES THE APP, in both the number form and the string form. Skipped loudly where PowerShell is absent,
  # since the reading is the whole check.
  if [ -z "$NAMED" ]; then
    echo "note: no PowerShell to read the version resource, so the app-naming half was not checked"
  elif [ "$NAMED" = "1.2.3.4|packcheck-versioned|packcheck-versioned" ]; then
    echo "ok: ...and the resource names the app, in the numbers and in the strings ($NAMED)"
  else
    echo "FAIL: the version resource does not name the app, it reads '$NAMED'" >&2
    status=1
  fi
  # A malformed version is refused by name rather than written as zeros.
  "$EXE" "$PACKER" "$LAUNCHER" "$VSUB" "$VPACKED" --version 1.2.3 > "$OUTBOX" 2>&1
  if [ $? -eq 2 ] && grep -q "four whole numbers" "$OUTBOX"; then
    echo "ok: a malformed --version is refused by name"
  else
    echo "FAIL: a malformed --version was not refused" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  rm -f "$VPACKED"; rm -rf "$(dirname "$VSUB")"
  # ── WHERE THE ENGINE IS FOUND: beside the exe, or a directory the user DECLARED (env) or put on PATH ────────────
  # A launcher alone in a folder, with the engine's folder on PATH and nothing beside it, must load that engine and say
  # which one it took. This is the shape a user asked for: ONE engine, many launchers.
  #
  # THE USER'S OWN ENVIRONMENT IS NOT PART OF THESE LEGS. An installed engine sets SLC_ENGINE_HOME machine-wide and puts
  # its folder on PATH, so a leg that assumes neither was measuring the INSTALLED engine instead of the one it built:
  # measured on a machine with the product installed, "a PATH-only engine was not found" (the announcement named
  # C:\Program Files\ScriptLangCore\bin) and the orphan leg HUNG, because with an engine reachable it started a REPL and
  # waited on stdin. Cleared for this section, restored after it, so the legs run the same on a bare box and on a box
  # where the product is installed.
  ENV_HOME_SAVED="${SLC_ENGINE_HOME-}"; ENV_DLL_SAVED="${SLC_ENGINE_DLL-}"
  unset SLC_ENGINE_HOME SLC_ENGINE_DLL
  SHARED="$(mktemp -d)"; ALONE="$(mktemp -d)"
  cp "$DLL" "$SHARED/ScriptLangCore.dll"
  printf 'print("engine from PATH ran");\n' > "$SHARED/probe.slc"
  cp "$LAUNCHER" "$ALONE/lonely.exe"
  # The launcher is a WINDOWS process, so PATH must be in ITS spelling: backslashes and `;`. A POSIX path silently
  # yields "no engine found", which is the shape a first attempt at this leg produced.
  SHARED_WIN="$(cygpath -w "$SHARED")"; ALONE_WIN="$(cygpath -w "$ALONE")"
  ( cd "$ALONE" && SLC_ENGINE_HOME= SLC_ENGINE_DLL= PATH="$SHARED_WIN;$PATH" ./lonely.exe "$SHARED_WIN\\probe.slc" ) > "$OUTBOX" 2>&1
  if [ $? -eq 0 ] && grep -qF "engine from PATH ran" "$OUTBOX" && grep -qF "engine from $SHARED_WIN\ScriptLangCore.dll" "$OUTBOX"; then
    echo "ok: a launcher alone in a folder finds the engine on PATH, and says which one"
  else
    echo "FAIL: a PATH-only engine was not found" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  # A DECLARED location that does not hold the engine is REFUSED BY NAME, never skipped in favour of another copy.
  ( cd "$ALONE" && SLC_ENGINE_HOME="$ALONE_WIN" ./lonely.exe "$SHARED_WIN\\probe.slc" ) > "$OUTBOX" 2>&1
  if [ $? -eq 3 ] && grep -q "SLC_ENGINE_HOME names" "$OUTBOX"; then
    echo "ok: SLC_ENGINE_HOME naming a folder with no engine is refused by name (exit 3)"
  else
    echo "FAIL: a declared engine folder without the DLL was not refused" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  # ...and a declared one that DOES hold it wins, even with another copy on PATH.
  ( cd "$ALONE" && SLC_ENGINE_HOME="$SHARED_WIN" ./lonely.exe "$SHARED_WIN\\probe.slc" ) > "$OUTBOX" 2>&1
  if [ $? -eq 0 ] && grep -q "engine from PATH ran" "$OUTBOX"; then
    echo "ok: SLC_ENGINE_HOME selects the engine explicitly"
  else
    echo "FAIL: SLC_ENGINE_HOME did not select the engine" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  # BUT A COPY BESIDE THE EXE WINS OVER THE ENVIRONMENT, which is the order a self-contained app needs: the installer
  # sets SLC_ENGINE_HOME machine-wide, so with the environment first a packaged app that SHIPS its own engine ran the
  # INSTALLED one instead (measured: "engine from <install>\ScriptLangCore.dll" while its own copy sat beside it), which
  # silently changes the engine version the app was built against. The discriminator is the announcement: it is printed
  # ONLY when the engine is not beside the exe.
  OWN="$(mktemp -d)"
  cp "$LAUNCHER" "$OWN/selfcontained.exe"; cp "$DLL" "$OWN/ScriptLangCore.dll"
  OWN_WIN="$(cygpath -w "$OWN")"
  ( cd "$OWN" && SLC_ENGINE_HOME="$SHARED_WIN" PATH="$SHARED_WIN;$PATH" ./selfcontained.exe "$SHARED_WIN\\probe.slc" ) > "$OUTBOX" 2>&1
  if [ $? -eq 0 ] && grep -qF "engine from PATH ran" "$OUTBOX" \
     && ! grep -qF "launcher: engine from" "$OUTBOX"; then
    echo "ok: a launcher WITH its own engine beside it uses that one, not the declared or PATH one"
  else
    echo "FAIL: a self-contained launcher was made to load another engine" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  rm -rf "$OWN"
  rm -rf "$SHARED" "$ALONE"
  # ── THE SUBSYSTEM BYTE: --gui writes 2 into the copy, --console leaves 3, and the copy still runs ──────────────────
  # The field is OptionalHeader+0x44, i.e. pe+0x5C for PE32 and PE32+ alike, and `od` reads it without a helper: a
  # u32 at 0x3C is e_lfanew, a u16 at that plus 0x5C is the subsystem.
  GSYS="$(mktemp -d)"
  cp "$LAUNCHER" "$GSYS/launcher.exe"; cp "$DLL" "$GSYS/ScriptLangCore.dll"
  printf 'print("gui copy ran");\n' > "$GSYS/gapp.slc"
  "$EXE" "$PACKER" "$GSYS/launcher.exe" "$GSYS/gapp.slc" "$GSYS/gapp.exe" --gui > "$GSYS/pack.log" 2>&1
  PACKED=$?
  PE="$(od -An -tu4 -j 60 -N 4 "$GSYS/gapp.exe" 2>/dev/null | tr -d ' ')"
  SUB="$(od -An -tu2 -j $((PE + 0x5C)) -N 2 "$GSYS/gapp.exe" 2>/dev/null | tr -d ' ')"
  ( cd "$GSYS" && ./gapp.exe ) > "$OUTBOX" 2>&1
  if [ $PACKED -eq 0 ] && [ "$SUB" = "2" ] && grep -qF "gui copy ran" "$OUTBOX"; then
    echo "ok: --gui writes subsystem 2 into the copy (pe+0x5C) and the copy still runs its payload"
  else
    echo "FAIL: --gui did not produce a GUI image that runs (packed=$PACKED subsystem=$SUB)" >&2
    cat "$GSYS/pack.log" "$OUTBOX" >&2
    status=1
  fi
  "$EXE" "$PACKER" "$GSYS/launcher.exe" "$GSYS/gapp.slc" "$GSYS/capp.exe" --console > "$OUTBOX" 2>&1
  PE="$(od -An -tu4 -j 60 -N 4 "$GSYS/capp.exe" | tr -d ' ')"
  SUB="$(od -An -tu2 -j $((PE + 0x5C)) -N 2 "$GSYS/capp.exe" | tr -d ' ')"
  # ...and NO flag must leave the launcher's own byte alone, which is what keeps a script that already packs working.
  "$EXE" "$PACKER" "$GSYS/launcher.exe" "$GSYS/gapp.slc" "$GSYS/napp.exe" > "$OUTBOX" 2>&1
  PE0="$(od -An -tu4 -j 60 -N 4 "$GSYS/launcher.exe" | tr -d ' ')"
  OWN="$(od -An -tu2 -j $((PE0 + 0x5C)) -N 2 "$GSYS/launcher.exe" | tr -d ' ')"
  PEN="$(od -An -tu4 -j 60 -N 4 "$GSYS/napp.exe" | tr -d ' ')"
  SUBD="$(od -An -tu2 -j $((PEN + 0x5C)) -N 2 "$GSYS/napp.exe" | tr -d ' ')"
  if [ "$SUB" = "3" ] && [ "$SUBD" = "$OWN" ]; then
    echo "ok: --console writes subsystem 3, and no flag leaves the launcher's own byte alone ($OWN)"
  else
    echo "FAIL: the subsystem byte is wrong (--console=$SUB, no-flag=$SUBD, launcher=$OWN)" >&2
    status=1
  fi
  rm -rf "$GSYS"
  # ── A LOOP SHOWS ONE ITERATION'S OUTPUT AND THEN A PERCENTAGE, in a SESSION too ─────────────────────────────────
  # `print` writes to the engine's own `Interpreter.Out`, and a session sets it, so silencing the iterations with
  # `Console.SetOut` alone left EVERY iteration flooding the host's pane: reported for .run/.looprun/.gc leak, where the
  # rule is one iteration's output and then a percentage.
  LPROG="$(mktemp -d)"; LPACK="$(dirname "$LAUNCHER")"
  printf 'print("iteration output");\n' > "$LPROG/chatty.slc"
  printf '.gc leak %s 4\n.exit\n' "$(cygpath -w "$LPROG/chatty.slc")" > "$LPROG/in.txt"
  ( cd "$LPACK" && ./ScriptLangCore.Launcher.exe --repl < "$LPROG/in.txt" > "$LPROG/out.txt" 2> "$LPROG/err.txt" )
  ONCE="$(grep -c "iteration output" "$LPROG/out.txt" || true)"
  if [ "$ONCE" = "1" ] && grep -q "Progress: " "$LPROG/err.txt"; then
    echo "ok: a loop prints ONE iteration's output and then a percentage, in a session too"
  else
    echo "FAIL: a loop flooded the pane ($ONCE iteration line(s)) or lost its progress" >&2
    head -6 "$LPROG/out.txt" >&2
    status=1
  fi
  rm -rf "$LPROG"
  # ── AND A LAUNCHER WITH NO ENGINE BESIDE IT SAYS SO INSTEAD OF FAILING TO LOAD ───────────────────────────────
  ORPHAN="$(mktemp -d)"
  cp "$LAUNCHER" "$ORPHAN/launcher-alone.exe"
  # A minimal PATH as well: the installed engine's folder is on the user's PATH on this kind of machine, and then an
  # orphan launcher legitimately finds an engine and waits in a REPL instead of refusing (measured: the leg HUNG).
  ( cd "$ORPHAN" && SLC_ENGINE_HOME= SLC_ENGINE_DLL= PATH="/c/Windows/system32:/c/Windows" ./launcher-alone.exe ) \
      > "$OUTBOX" 2>&1 < /dev/null
  orphanStatus=$?
  if [ "$orphanStatus" -eq 3 ] && grep -q "ScriptLangCore.dll" "$OUTBOX"; then
    echo "ok: a launcher with no engine beside it refuses, naming ScriptLangCore.dll (exit 3)"
  else
    echo "FAIL: an orphan launcher answered $orphanStatus" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  # ── AND AN ORPHAN LAUNCHER STARTED THE WAY EXPLORER STARTS IT SHOWS A BOX ───────────────────────────────────────
  # The leg above proves the WORDS; this one proves that a person sees them, which is a different question and was
  # reported broken: the launcher is a CONSOLE image, so a double-click gives it a console of its own, the words went
  # there, and that console died with the process (measured: exit 3 and nothing on screen). `Fatal` therefore asks
  # GetConsoleProcessList, and a count of ONE means the console is ours and will vanish, so a box follows. This leg starts
  # the copy with Start-Process (ShellExecute, so a console image gets its own console, the double-click shape) and looks
  # for the dialog by its caption.
  if command -v pwsh >/dev/null 2>&1 || command -v powershell >/dev/null 2>&1; then
    PSHELL="$(command -v pwsh 2>/dev/null || command -v powershell)"
    cat > "$ORPHAN/box.ps1" <<'PS1'
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
'@
$p = Start-Process -FilePath $args[0] -PassThru
$found = $false
for ($i = 0; $i -lt 50 -and -not $found; $i++) {
  Start-Sleep -Milliseconds 100
  $h = [W.U]::FindWindowW('#32770', 'ScriptLangCore: the engine DLL is missing')
  if ($h -ne [IntPtr]::Zero) {
    $found = $true
    [void][W.U]::SendMessageW($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  }
}
if ($p.WaitForExit(10000)) { "box=$found exit=$($p.ExitCode)" } else { $p.Kill(); "box=$found exit=timeout" }
PS1
    BOXLINE="$(PATH="/c/Windows/system32:/c/Windows" SLC_ENGINE_HOME= SLC_ENGINE_DLL= "$PSHELL" -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ORPHAN/box.ps1")" "$(cygpath -w "$ORPHAN/launcher-alone.exe")" 2>&1 | tr -d '\r')"
    if [ "$BOXLINE" = "box=True exit=3" ]; then
      echo "ok: an orphan launcher double-clicked shows a box, and exiting it answers 3"
    else
      echo "FAIL: an orphan launcher showed no box, or did not answer 3 ($BOXLINE)" >&2
      status=1
    fi
  else
    echo "note: the orphan launcher's box not checked (no powershell on this box)" >&2
  fi
  rm -rf "$ORPHAN"
  # The environment comes back as it was found, so a later leg is not measured through this section's clearing.
  if [ -n "$ENV_HOME_SAVED" ]; then export SLC_ENGINE_HOME="$ENV_HOME_SAVED"; fi
  if [ -n "$ENV_DLL_SAVED" ]; then export SLC_ENGINE_DLL="$ENV_DLL_SAVED"; fi
else
  echo "note: packaging not checked (needs $LAUNCHER, $EXE and $PACKER)" >&2
fi

# THE SQLITE LEG: the same DLL loaded as a SQLite extension, exercised through the engine's OWN sqlite namespace (so
# this needs no SQLite shell on the machine). It proves the export exists, that the api table resolves against a host
# that EXPORTS the routine names (the verification branch), and that a script's result comes back as JSON.
if [ -f "$EXE" ] && [ -f "$DLL" ]; then
  SQEXT_DIR="$(mktemp -d)"
  SQEXT_SUBJECT="$SQEXT_DIR/sqlite-extension.slc"
  # The path must be a WINDOWS one for the loader (a POSIX /d/... name is not a file SQLite can open) and must carry
  # FORWARD slashes, because it is interpolated into a script string literal where a backslash is an escape.
  SQEXT_POSIX="$(cd "$(dirname "$DLL")" && pwd)/$(basename "$DLL")"
  if command -v cygpath >/dev/null 2>&1; then
    SQEXT_DLL="$(cygpath -m "$SQEXT_POSIX")"
  else
    SQEXT_DLL="$SQEXT_POSIX"
  fi
  cat > "$SQEXT_SUBJECT" <<SLC
var lib = LoadLibrary("sqlite3");
var db = Sqlite.OpenDb(":memory:");
print("loaded=" + Sqlite.LoadExtensions(lib, db, "$SQEXT_DLL"));
foreach (var row in Sqlite.Query(lib, db, "select slc_eval('a0 + a1', 2, 3) as j, json_extract(slc_eval('a0 * 2', 21), '\$.ReturnValue') as v")) {
  print("result=" + row["j"]);
  print("extracted=" + row["v"]);
}
// The other three registrations: a native value, a fold, and a file whose function the connection then calls.
foreach (var row in Sqlite.Query(lib, db, "select slc_eval_scalar('a0 * 2', 21) as s, typeof(slc_eval_scalar('a0 * 2', 21)) as t")) {
  print("scalar=" + row["s"] + " type=" + row["t"]);
}
foreach (var row in Sqlite.Query(lib, db, "with recursive t(n) as (select 1 union all select n+1 from t where n<5) select slc_agg('(a0 ?? 0) + a1', n) as total from t")) {
  print("agg=" + row["total"]);
}
// The set form: one call for a whole array, read back with SQLite's own json_each.
foreach (var row in Sqlite.Query(lib, db, "select sum(value) as total, count(*) as rows from json_each(slc_map('a0 * 3', '[1,2,3,4,5]'))")) {
  print("map=" + row["total"] + " rows=" + row["rows"]);
}
// The aggregate set form: rows from a query, two arguments per row, and the refusal of a missing one.
foreach (var row in Sqlite.Query(lib, db, "with t(a,b) as (values (1,10),(2,20)) select sum(value) as total, count(*) as rows from json_each((select slc_map_agg('a0 + a1', a, b) from t))")) {
  print("mapagg=" + row["total"] + " rows=" + row["rows"]);
}
SLC
  # A script the connection loads and then calls, which is the shape a schema carries its logic in.
  cat > "$SQEXT_DIR/grades.slc" <<'SLC'
int Grade(int score) {
    if (score >= 90) { return 4; }
    if (score >= 80) { return 3; }
    return 2;
}
SLC
  sqextGrades="$(cygpath -m "$SQEXT_DIR/grades.slc" 2>/dev/null || echo "$SQEXT_DIR/grades.slc")"
  # A SUITE'S VERDICT IS ITS CHECK REPORT, on this door too: `CheckSummary(); exit(CheckFailed())` answers the SCRIPT's
  # own code, so the failing check used to exist only in stdout a SQL caller never sees.
  cat > "$SQEXT_DIR/suite.slc" <<'SLC'
Check("this test will fail", 1 == 0);
CheckSummary();
exit(CheckFailed());
SLC
  sqextSuite="$(cygpath -m "$SQEXT_DIR/suite.slc" 2>/dev/null || echo "$SQEXT_DIR/suite.slc")"
  # A COMPILED BUNDLE THROUGH THE SAME DOOR, because `slc_file` used to hand a `.slcbc`'s BINARY header to the lexer and
  # answered `Unexpected character '\u001E'` at line 1, column 5 (measured). The bundle is written by the engine's own
  # exporter with the strict strip, and the check is that the door answers what the FILE answers: the value of the last
  # top-level expression statement (an explicit top-level `return` travels too, which is the older half).
  cat > "$SQEXT_DIR/last.slc" <<'SLC'
print("bundle subject ran");
var v = 7;
v + 1;
SLC
  sqextLast="$(cygpath -m "$SQEXT_DIR/last.slc" 2>/dev/null || echo "$SQEXT_DIR/last.slc")"
  sqextLastBc="$(cygpath -m "$SQEXT_DIR/last.slcbc" 2>/dev/null || echo "$SQEXT_DIR/last.slcbc")"
  cat >> "$SQEXT_SUBJECT" <<SLC
var bundleReport = Bytecode.ExportProgram("$sqextLastBc", File.ReadAllText("$sqextLast"), { "source": "strip" });
print("bundleReport=" + bundleReport);
foreach (var row in Sqlite.Query(lib, db, "select json_extract(slc_file('$sqextLastBc'), '\$.ReturnValue') as a, json_extract(slc_file('$sqextLast'), '\$.ReturnValue') as b")) {
  print("bundleAnswer=" + row["a"] + " sourceAnswer=" + row["b"]);
}
SLC
  cat >> "$SQEXT_SUBJECT" <<SLC
foreach (var row in Sqlite.Query(lib, db, "select json_extract(slc_file('$sqextGrades'), '\$.HasError') as loaded, slc_eval_scalar('Grade(a0)', 85) as grade")) {
  print("fileError=" + row["loaded"] + " grade=" + row["grade"]);
}
// A SUITE'S ANSWER IS ITS CHECK REPORT, through the SQL door too: the failure count, the label and the exit code.
foreach (var row in Sqlite.Query(lib, db, "select json_extract(j, '\$.ReturnValue.Failed') as failed, json_extract(j, '\$.ReturnValue.Checks[0].Label') as label, json_extract(j, '\$.ExitCode') as code from (select slc_file('$sqextSuite') as j)")) {
  print("suiteReport=" + row["failed"] + "|" + row["label"] + "|" + row["code"]);
}
// A RUN OWNS ITS OWN VERDICT ON THIS DOOR TOO: `exitcode` is process state, so a subject ending `exit(3)` used to make
// EVERY later `slc_file` on the connection answer ExitCode 3, including one with no exit at all.
foreach (var row in Sqlite.Query(lib, db, "select json_extract(j, '\$.ExitCode') as code from (select slc_file('$sqextSuite') as j)")) { }
foreach (var row in Sqlite.Query(lib, db, "select json_extract(j, '\$.ExitCode') as code from (select slc_file('$sqextLast') as j)")) {
  print("exitLeak=" + row["code"]);
}
// THE DECLARED ARITY IS PART OF THE CONTRACT. pragma_function_list is what a reader asks, and a truthful number makes
// SQLite refuse a call with the wrong COUNT itself, before this extension's code runs: an extra argument used to be
// accepted and silently IGNORED (measured: slc_agg('(a0 ?? 0) + a1', 5, 999) answered 5). Two of the six are genuinely
// variadic (slc_eval reads a0..aN, slc_map_agg sees every row's a0..aN), so they keep -1 and their own refusal for a
// call with no arguments at all, which SQLite cannot express. NO BACKTICKS IN THIS HEREDOC: it is unquoted, so a
// backtick would run its contents as a shell command (measured: "pragma_function_list: command not found").
foreach (var row in Sqlite.Query(lib, db, "select group_concat(x, ' ') as a from (select name || '=' || narg as x from pragma_function_list where name like 'slc_%' order by name)")) {
  print("arity=" + row["a"]);
}
var extraArgs = "none";
try { Sqlite.Query(lib, db, "select slc_map('a0', '[1]', 'extra')"); } catch (var e) { extraArgs = e.Message; }
print("extraArgsRefused=" + extraArgs.Contains("wrong number of arguments"));
var fileArgs = "none";
try { Sqlite.Query(lib, db, "select slc_file('a.slc', 'b.slc')"); } catch (var e) { fileArgs = e.Message; }
print("fileArityRefused=" + fileArgs.Contains("wrong number of arguments"));
SLC
  # THE TWO AVRO DOORS: a container stored as a BLOB, read back as the SCHEMA it carries and as PURE JSON that SQLite's
  # own json functions then process. The container is ZSTANDARD-compressed on purpose, because the point of the pair is
  # that the codec is the FILE's own: the JSON below can only be right if the block was decompressed with it.
  cat >> "$SQEXT_SUBJECT" <<SLC
public class AvroRow {
    public long Id;
    public string Name = "";
}
var avroSchema = Avro.Schema(AvroRow);
var avroRows = [].ToList();
var avroOne = new AvroRow(); avroOne.Id = 1; avroOne.Name = "one"; avroRows.Add(avroOne);
var avroTwo = new AvroRow(); avroTwo.Id = 2; avroTwo.Name = "two"; avroRows.Add(avroTwo);
var avroContainer = Avro.WriteContainer(avroRows, avroSchema, { "codec": "zstandard" });
foreach (var row in Sqlite.Query(lib, db, "create table files (id integer, data blob)")) { }
foreach (var row in Sqlite.Query(lib, db, "insert into files values (?, ?)", [1, avroContainer])) { }
foreach (var row in Sqlite.Query(lib, db, "select json_extract(slc_avro_json(data), '\$[1].Name') as name, json_extract(slc_avro_json(data), '\$[0].Id') as id from files")) {
  print("avroJson=" + row["name"] + "|" + row["id"]);
}
foreach (var row in Sqlite.Query(lib, db, "select json_extract(slc_avro_schema(data), '\$.name') as record, (select count(*) from json_each(slc_avro_json(data))) as rows from files")) {
  print("avroSchema=" + row["record"] + " rows=" + row["rows"]);
}
var avroRefusal = "none";
// A UDF's refusal arrives at the STEP, so the statement has to be ITERATED for it to be raised: a bare call only
// prepares it (measured: the same line without the loop answered no error at all).
try { foreach (var row in Sqlite.Query(lib, db, "select slc_avro_json('not bytes')")) { } } catch (var e) { avroRefusal = e.Message; }
print("avroBlobRefused=" + avroRefusal.Contains("BYTES"));
SLC
  OUTBOX="$(mktemp)"
  "$EXE" "$SQEXT_SUBJECT" > "$OUTBOX" 2>&1
  sqextStatus=$?
  if [ "$sqextStatus" -eq 0 ] && grep -q "loaded=True" "$OUTBOX" \
     && grep -q '"HasError":false' "$OUTBOX" && grep -q '"ReturnValue":5' "$OUTBOX" \
     && grep -q "extracted=42" "$OUTBOX" && grep -q "scalar=42 type=integer" "$OUTBOX" \
     && grep -q "agg=15" "$OUTBOX" && grep -q "fileError=0 grade=3" "$OUTBOX" \
     && grep -q "map=45 rows=5" "$OUTBOX" && grep -q "mapagg=33 rows=2" "$OUTBOX" \
     && grep -q "source stripped" "$OUTBOX" && grep -q "bundleAnswer=8 sourceAnswer=8" "$OUTBOX" \
     && grep -qF "arity=slc_agg=2 slc_avro_json=1 slc_avro_schema=1 slc_eval=-1 slc_eval_scalar=2 slc_file=1 slc_map=2 slc_map_agg=-1" "$OUTBOX" \
     && grep -q "extraArgsRefused=True" "$OUTBOX" && grep -q "fileArityRefused=True" "$OUTBOX" \
     && grep -qF "avroJson=two|1" "$OUTBOX" && grep -qF "avroSchema=AvroRow rows=2" "$OUTBOX" \
     && grep -q "avroBlobRefused=True" "$OUTBOX" \
     && grep -qF "suiteReport=1|this test will fail|1" "$OUTBOX" && grep -qF "exitLeak=0" "$OUTBOX"; then
    echo "ok: the DLL run as a SQLite extension answered all eight functions, declares their real arities, refuses a wrong argument count, slc_file answers the same value for a compiled bundle as for its source, a suite's answer is its check report, a Zstandard-compressed Avro container reads back as its schema and as pure JSON, and a run's exit code does not leak to the next one"
  else
    echo "FAIL: the SQLite extension leg answered $sqextStatus" >&2
    cat "$OUTBOX" >&2
    status=1
  fi
  rm -rf "$SQEXT_DIR" "$OUTBOX"
else
  echo "note: the SQLite extension leg not checked (needs both $EXE and $DLL)" >&2
fi

# THE COM LEG: the sqlite3 shell's OWN thread never initialized COM, so Windows reports an IMPLICIT MTA apartment for
# it and this engine refuses a COM call there with "COM Automation requires an STA thread" (measured: CoGetApartmentType
# answers type 1 with qualifier 1). `init_sta` calls OleInitialize on that thread first, which is allowed exactly
# because nobody chose MTA, and the SAME script then answers. Only the PAIR proves the entry point is what decides it,
# which is why both halves are asserted. The server is `Scripting.FileSystemObject`, in process, so no Excel and no
# registration is involved. The shell ships beside the DLL, so this needs no SQLite on the machine.
SHELL_EXE="$(dirname "$DLL")/sqlite3.exe"
if [ -x "$SHELL_EXE" ]; then
  COM_DIR="$(mktemp -d)"
  COM_SUBJECT="$COM_DIR/com.slc"
  cat > "$COM_SUBJECT" <<'SLC'
var drives = 0;
Com.Run(fun (scope) {
  var fso = scope.CreateObject("Scripting.FileSystemObject");
  drives = fso.Drives.Count;
});
"com=" + (drives > 0);
SLC
  COM_SUBJECT_WIN="$(cygpath -w "$COM_SUBJECT" 2>/dev/null || printf '%s' "$COM_SUBJECT")"
  DLL_WIN="$(cygpath -w "$DLL" 2>/dev/null || printf '%s' "$DLL")"
  cat > "$COM_DIR/sta.sql" <<SQL
.load $DLL_WIN init_sta
.mode list
select json_extract(slc_file('$COM_SUBJECT_WIN'), '\$.ReturnValue');
SQL
  cat > "$COM_DIR/plain.sql" <<SQL
.load $DLL_WIN init
.mode list
select json_extract(slc_file('$COM_SUBJECT_WIN'), '\$.ErrorMessage');
SQL
  STA_ANSWER="$("$SHELL_EXE" ':memory:' < "$COM_DIR/sta.sql" 2>&1)"
  PLAIN_ANSWER="$("$SHELL_EXE" ':memory:' < "$COM_DIR/plain.sql" 2>&1)"
  case "$STA_ANSWER" in *'com=True'*) staOk=1 ;; *) staOk=0 ;; esac
  case "$PLAIN_ANSWER" in *'requires an STA thread'*) plainOk=1 ;; *) plainOk=0 ;; esac
  if [ "$staOk" -eq 1 ] && [ "$plainOk" -eq 1 ]; then
    echo "ok: init_sta made the shell's thread the main STA so a COM script answered, where plain init refuses it"
  else
    echo "FAIL: the COM leg answered sta=$staOk plain=$plainOk" >&2
    printf 'init_sta -> %s\ninit -> %s\n' "$STA_ANSWER" "$PLAIN_ANSWER" >&2
    status=1
  fi
  rm -rf "$COM_DIR"
else
  echo "note: the COM leg not checked (no sqlite3 shell beside '$DLL')" >&2
fi

if [ "$status" -eq 0 ]; then
  echo "ok: the C ABI answered every check against '$DLL'"
else
  echo "FAIL: the embedding check answered $status (see the failing line above)" >&2
fi

exit "$status"
