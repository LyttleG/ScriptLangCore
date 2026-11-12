@ECHO OFF
REM NativeAOT build, for BOTH artifacts this project now has:
REM
REM   the EXECUTABLE  bin\Release\net11.0\win-x64\publish\ScriptLangCore.exe   (the gate reads this one,
REM                   src/build/run-repl-tests.sh --build aot), mirrored into publish-aot\ beside sqlite3.dll,
REM                   assets\ and tools\, which is the SHIPPING layout
REM   the EMBEDDING   publish-dll\ScriptLangCore.dll   (a NativeAOT SHARED library, plus the .lib/.exp an import
REM                   library needs; a host loads it and calls the C ABI, see src\Host\)
REM   the LAUNCHER    publish-launcher\ScriptLangCore.Launcher.exe   (a ~60 KB C exe that holds NO engine code and
REM                   drives the DLL through that same C ABI, from src\embed\launcher-c\; the C# original it was ported
REM                   from stays in src\embed\launcher\ as the reference)
REM
REM THE SOLUTION HOLDS THREE PROJECTS (`ScriptLangCore`, `src\embed\` the C ABI host, `src\embed\launcher\` the C#
REM launcher), so EVERY dotnet command here NAMES its project: a bare `dotnet publish` resolves to the SOLUTION and
REM publishes all three, which is slower, lands artifacts in the wrong places and builds the harness in a
REM configuration nobody asked for. Same reason the DLL leg below is explicit, which is the one that already paid for
REM this (NETSDK1194, an output directory beside a solution).
REM
REM Why the exe publishes to the default location: `-o publish-aot` is what this batch used to do, and it left the
REM GATE blind, because that path is not one run-repl-tests.sh looks at, so a fresh AOT build was silently ignored in
REM favour of an older one. Publishing to the default location and MIRRORING it into publish-aot keeps the shipping
REM folder fresh with one build. Note /E, not /MIR: the project READS publish-aot\sqlite3.dll while building, so a
REM mirror that deletes would be able to remove the very file the next build needs.
REM
REM Why the DLL is a CONFIGURATION (`-c Embed`) rather than a pile of -p: overrides: a configuration gets its own
REM obj\ and bin\ directory by construction, so the two AOT legs can never be handed each other's managed assembly by
REM an up-to-date check (a library assembly carries no entry point, so an exe publish that reused it would fail or,
REM worse, link something stale). The project declares both AOT configurations against one profile property
REM (`SlcAotProfile`), so the optimization knobs are stated once for both artifacts.
REM
REM Why the DLL cannot be published by compile_jit.bat: `[UnmanagedCallersOnly]` exports are emitted by the AOT
REM compiler, so a JIT build of these same sources is an ordinary managed library with NO exports at all.

REM Using PWSH to spam test our weberver:  1..10000 | ForEach-Object { Invoke-RestMethod http://localhost:8765/todos }

REM dotnet clean
rem The project is NAMED, not implied: with the solution holding three projects (`ScriptLangCore`, `src/embed/` and
rem `src/embed/launcher/`), a bare `dotnet publish` resolves to the SOLUTION and publishes all of them, which is not
rem what this leg is for and would build the harness in the wrong configuration.
dotnet publish ScriptLangCore.csproj -c Release -r win-x64 --self-contained
if errorlevel 1 goto :failed

rem The MCP server EXECUTES every tools\*.slc it finds at startup, so a script left behind by an earlier layout still
rem runs there: a probe in that folder hung or killed the server (measured 2026-10-08). robocopy /E copies but never
rem deletes, so drop the tool scripts first. BOTH spellings, because the extension was renamed `.script` -> `.slc` and a
rem mirror from before that rename still holds the old one. The non-script files stay, because the project READS
rem publish-aot\sqlite3.dll while building and a mirror that deletes could take it away.
if exist "publish-aot\tools" del /q "publish-aot\tools\*.slc" 2>nul
if exist "publish-aot\tools" del /q "publish-aot\tools\*.script" 2>nul

rem A CORPUS REORGANISATION LEAVES PRE-MOVE COPIES BEHIND, and robocopy /E copies but never deletes: after a suite
rem moves, the deployed assets\ tree holds it under BOTH the old flat path and the new one, and a tool that scans ONE
rem folder (the batch runner's Directory.GetFiles(dir, "*.slc")) then runs both. Measured 2026-11-12: a nested
rem htmx_app_test.slc staged mid-move still walked "..\htmx" while the repository's copy walks "..\..\htmx", so the
rem deployed copy failed with "Directory not found: ...\assets\language\repl\htmx\htmx_todo.slc" for a reason the
rem repository no longer has, and the same duplicate ran under its pre-move name. assets\ is generated ENTIRELY from
rem the repository (the project copies it as Content), so removing it before the copy is exact rather than a guess.
if exist "publish-aot\assets" rmdir /s /q "publish-aot\assets"

robocopy "bin\Release\net11.0\win-x64\publish" "publish-aot" /E /NFL /NDL /NJH /NJS /NP
if errorlevel 8 goto :failed

rem THE INSTRUMENTS ARE NOT SHIPPED. In the source tree `tools\` holds only what a SCRIPT needs at run time (the mcp
rem scripts, sqlite3.dll and its readme); the checks, the harness runners and their baselines live in `src\build\` and
rem are not content items at all. A publish made BEFORE that split still holds them, and robocopy /E copies but never
rem deletes, so a deployed folder would keep shipping a checker. They are dropped by EXTENSION, which is what tells an
rem instrument from the shipped set, and the readme is put back because it IS shipped.
if exist "publish-aot\tools" del /q "publish-aot\tools\*.sh" 2>nul
if exist "publish-aot\tools" del /q "publish-aot\tools\*.ps1" 2>nul
if exist "publish-aot\tools" del /q "publish-aot\tools\*.bat" 2>nul
if exist "publish-aot\tools" del /q "publish-aot\tools\*.cmd" 2>nul
if exist "publish-aot\tools" del /q "publish-aot\tools\*.txt" 2>nul
if exist "tools\readme.txt" copy /y "tools\readme.txt" "publish-aot\tools\readme.txt" >nul

rem THE EMBEDDING ARTIFACT. `-c Embed` is declared in the project: OutputType=Library plus NativeLib=Shared is what
rem makes the AOT compiler emit a DLL instead of an exe, with its own obj\Embed\ and bin\Embed\. The project is named
rem explicitly so this publish cannot pick up the SOLUTION, which is what a bare `dotnet publish -o <dir>` does (it
rem then warns NETSDK1194 about an output directory beside a solution).
dotnet publish ScriptLangCore.csproj -c Embed -r win-x64 --self-contained -o publish-dll
if errorlevel 1 goto :failed

rem THE LAUNCHER: the C port, built by its own script, plus the engine beside it so the folder is runnable as published
rem (`ScriptLangCore.Launcher.exe myscript.slc`). It speaks the same C ABI as the C# version it was ported from, and it
rem is ~60 KB instead of the 1.2 MB NativeAOT needed for the same program (the C# project stays in
rem src\embed\launcher\ as the reference, and nothing here publishes it any more).
rem
rem THE BUILD REFUSES A 32-BIT gcc BY NAME: the `gcc` first on PATH on this box targets i686-w64-mingw32, and a 32-bit
rem launcher cannot load the win-x64 engine at all (Windows answers "module not found" for an architecture mismatch,
rem which reads like a missing engine). src\embed\launcher-c\build.bat probes for an x86_64 one and says which to
rem install if there is none, and it verifies the image it produced (machine 0x8664, subsystem 3).
rem The .NET launcher's own files (`deps.json`, `runtimeconfig.json`, a `pdb`) are removed first: this folder carried
rem them while the launcher was managed, and a native exe beside a runtimeconfig.json reads as a half-finished layout.
if exist publish-launcher\ScriptLangCore.Launcher.deps.json del /q publish-launcher\ScriptLangCore.Launcher.deps.json >nul 2>nul
if exist publish-launcher\ScriptLangCore.Launcher.runtimeconfig.json del /q publish-launcher\ScriptLangCore.Launcher.runtimeconfig.json >nul 2>nul
if exist publish-launcher\ScriptLangCore.Launcher.pdb del /q publish-launcher\ScriptLangCore.Launcher.pdb >nul 2>nul
call src\embed\launcher-c\build.bat publish-launcher\ScriptLangCore.Launcher.exe
if errorlevel 1 goto :failed
rem The engine's deployed layout, beside the DLL: assets\ and tools\ are read by scripts, sqlite3.dll is loaded by NAME.
copy /y publish-dll\ScriptLangCore.dll publish-launcher\ >nul
if errorlevel 1 goto :failed
rem THE SAME PRUNE ONCE MORE, because the launcher's copy is made FROM publish-aot and robocopy /E never deletes either:
rem a suite that moved (or was deleted) here would keep its old copy beside the launcher, where the batch runner — which
rem scans ONE folder — would run both. Both trees are generated, so removing them before the copy is exact.
if exist "publish-launcher\assets" rmdir /s /q "publish-launcher\assets"
if exist "publish-launcher\tools" rmdir /s /q "publish-launcher\tools"
robocopy publish-aot\assets publish-launcher\assets /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :failed
robocopy publish-aot\tools publish-launcher\tools /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :failed
if exist publish-aot\sqlite3.dll copy /y publish-aot\sqlite3.dll publish-launcher\sqlite3.dll >nul

echo.
echo   gate build: bin\Release\net11.0\win-x64\publish\ScriptLangCore.exe
echo   shipping:   publish-aot\ScriptLangCore.exe
echo   embedding:  publish-dll\ScriptLangCore.dll (+ the .lib an import library needs)
echo   launcher:   publish-launcher\ScriptLangCore.Launcher.exe (+ the DLL and its assets beside it)
echo.
rem THE BATCH VERIFIES ITS OWN STAGING, because a COPY is the one thing that goes stale silently: the launcher loads the
rem DLL beside ITSELF, so a republished publish-dll alone leaves every launcher leg (and every suite a launcher child
rem runs) measuring an OLDER engine, which reads as "the fix does nothing". src/build/embed-check.sh refuses that drift; this
rem is the other end of the same rule, checked where the copy is made.
powershell -NoProfile -Command ^
  "$p='publish-dll\ScriptLangCore.dll'; $s='publish-launcher\ScriptLangCore.dll';" ^
  "if (-not (Test-Path $s)) { Write-Host 'STAGING CHECK FAILED: no DLL beside the launcher'; exit 1 };" ^
  "$h1=[System.Security.Cryptography.SHA256]::Create(); $a=[System.BitConverter]::ToString($h1.ComputeHash([System.IO.File]::ReadAllBytes($p))); $h1.Dispose();" ^
  "$h2=[System.Security.Cryptography.SHA256]::Create(); $b=[System.BitConverter]::ToString($h2.ComputeHash([System.IO.File]::ReadAllBytes($s))); $h2.Dispose();" ^
  "if (-not $a -or -not $b) { Write-Host 'STAGING CHECK FAILED: a hash could not be computed, so the two files were NOT compared'; exit 1 };" ^
  "if ($a -ne $b) { Write-Host 'STAGING CHECK FAILED: publish-launcher\ScriptLangCore.dll is not the published one'; exit 1 };" ^
  "foreach ($f in @('publish-aot\ScriptLangCore.exe','publish-dll\ScriptLangCore.dll','publish-launcher\ScriptLangCore.Launcher.exe','publish-launcher\ScriptLangCore.dll')) {" ^
  "  $i=Get-Item $f; Write-Host ('  {0,-46} {1,12:N0} B  {2:yyyy-MM-dd HH:mm:ss}' -f $f, $i.Length, $i.LastWriteTime) }" ^
  "$newestSource = Get-ChildItem -Path 'src' -Recurse -Filter '*.cs' -File | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' -and $_.FullName -notmatch '\\src\\embed\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1;" ^
  "if (-not $newestSource) { Write-Host 'FRESHNESS CHECK FAILED: no source found under src\, so the artifacts were NOT compared (run this batch from the repository root)'; exit 1 };" ^
  "$stale = @(); foreach ($f in @('publish-aot\ScriptLangCore.exe','publish-dll\ScriptLangCore.dll','publish-launcher\ScriptLangCore.Launcher.exe','publish-launcher\ScriptLangCore.dll')) { if ((Get-Item $f).LastWriteTime -lt $newestSource.LastWriteTime) { $stale += $f } };" ^
  "if ($stale.Count -gt 0) { Write-Host ('FRESHNESS CHECK FAILED: older than src\{0} ({1:yyyy-MM-dd HH:mm:ss}), so this leg did NOT publish: {2}' -f $newestSource.Name, $newestSource.LastWriteTime, ($stale -join ', ')); exit 1 };" ^
  "Write-Host ('  freshness check: every artifact is newer than the newest source (src\{0} at {1:yyyy-MM-dd HH:mm:ss})' -f $newestSource.Name, $newestSource.LastWriteTime);" ^
  "Write-Host ('  staging check: the DLL beside the launcher IS the published one (sha256 {0}...)' -f $a.Substring(0,16))"
if errorlevel 1 goto :failed
echo   NOTE: `dotnet clean` above removed bin\Debug, so a JIT run needs `dotnet build -c Debug -v q --nologo`
echo   first (src/build/run-repl-tests.sh reports exit=127 with an absent exe, which reads like a suite failure).
exit /b 0

:failed
echo.
echo   PUBLISH FAILED. NONE of the artifacts is fresh: bin\Release\net11.0\win-x64\publish\, publish-aot\,
echo   publish-dll\ and publish-launcher\ still hold the PREVIOUS build, and src/build/run-repl-tests.sh REFUSES a publish
echo   older than src\ rather than testing it. Read the error above rather than assuming the binary on disk is new.
exit /b 1
