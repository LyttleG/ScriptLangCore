@ECHO OFF
REM THE C LAUNCHER'S BUILD: launcher.c plus launcher.rc, through an x86_64 MinGW-w64 GCC, into one console EXE.
REM
REM USAGE: build.bat [output.exe]
REM   no argument        src\embed\launcher-c\build\ScriptLangCore.Launcher.exe
REM   an argument        exactly that path (compile_aot.bat passes publish-launcher\ScriptLangCore.Launcher.exe)
REM
REM AN x86_64 COMPILER IS REQUIRED, and the check for it is the point of half this file: the `gcc` that is first on PATH
REM on this box targets i686-w64-mingw32 (a 32-bit image), and a 32-bit launcher cannot load the engine's win-x64 DLL at
REM all: Windows answers "the specified module could not be found" for an architecture mismatch, which reads like a
REM missing engine and is not one. The toolchain is therefore probed and REFUSED BY NAME rather than taken from PATH
REM blindly.
REM
REM WHERE IT LOOKS, in order: SLC_MINGW_BIN (a directory override) -> %ProgramFiles%\msys64\mingw64\bin ->
REM C:\msys64\mingw64\bin -> each `gcc` on PATH whose `-dumpmachine` says x86_64.

SETLOCAL ENABLEEXTENSIONS

SET "HERE=%~dp0"
IF "%HERE:~-1%"=="\" SET "HERE=%HERE:~0,-1%"
SET "OUT=%~1"
IF NOT DEFINED OUT SET "OUT=%HERE%\build\ScriptLangCore.Launcher.exe"

REM The repository root, which is where `app.manifest` and `forward.ico` live: the SAME two files the C# launcher and the
REM engine use, so there is one statement of each rather than a copy that drifts.
PUSHD "%HERE%\..\..\.."
IF ERRORLEVEL 1 (
  ECHO   C LAUNCHER BUILD FAILED: cannot reach the repository root from "%HERE%".
  EXIT /B 1
)
SET "ROOT=%CD%"
POPD

REM ── THE TOOLCHAIN ─────────────────────────────────────────────────────────────────────────────────────────────────
REM EVERY candidate goes through :TryGcc, including the explicit override: the check that matters is what the compiler
REM SAYS it targets, not where it was found. Measured before this rule: SLC_MINGW_BIN pointed at the 32-bit toolchain was
REM accepted, and the build then died with `windres: Can't detect target endianness and architecture`, which names
REM nothing a reader can act on.
SET "GCC="
SET "REJECTED="
REM AN EXPLICIT OVERRIDE IS OBEYED, NOT CONSULTED, which is the rule the launcher itself applies to SLC_ENGINE_HOME: a
REM folder a user NAMED must hold a working x86_64 compiler, and quietly using another one would hide the typo. Without
REM the override, the well-known locations are tried in order and then PATH, which is a search.
IF DEFINED SLC_MINGW_BIN (
  IF NOT EXIST "%SLC_MINGW_BIN%\gcc.exe" GOTO :bad_override
  CALL :TryGcc "%SLC_MINGW_BIN%\gcc.exe"
  IF NOT DEFINED GCC GOTO :bad_override
) ELSE (
  IF EXIST "%ProgramFiles%\msys64\mingw64\bin\gcc.exe" CALL :TryGcc "%ProgramFiles%\msys64\mingw64\bin\gcc.exe"
  IF NOT DEFINED GCC IF EXIST "C:\msys64\mingw64\bin\gcc.exe" CALL :TryGcc "C:\msys64\mingw64\bin\gcc.exe"
  IF NOT DEFINED GCC FOR /F "delims=" %%G IN ('where gcc 2^>nul') DO IF NOT DEFINED GCC CALL :TryGcc "%%G"
)
IF NOT DEFINED GCC GOTO :no_compiler

FOR %%B IN ("%GCC%") DO SET "BINDIR=%%~dpB"
SET "WINDRES=%BINDIR%windres.exe"
IF NOT EXIST "%WINDRES%" (
  ECHO   C LAUNCHER BUILD FAILED: "%GCC%" was found but there is no windres.exe beside it, and the manifest, the icon
  ECHO   and the version have to be compiled in. Install the whole toolchain: pacman -S mingw-w64-x86_64-gcc
  EXIT /B 1
)

REM THE TOOLCHAIN'S OWN FOLDER GOES ON PATH, and it is not tidiness: `gcc.exe` starts `cc1.exe` from its lib\gcc folder,
REM and that program needs libwinpthread, libisl, libmpc, libmpfr, libgmp and libzstd FROM THE SAME BIN. Measured on this
REM box with msys64's mingw64 toolchain: `gcc -c hello.c` answered exit code 1 and NOTHING AT ALL, which reads exactly
REM like a compiler that refused the source, and with that folder on PATH the same command answers 0. `windres` fails the
REM same way one step earlier (its "preprocessing failed" is gcc failing to start).
SET "PATH=%BINDIR%;%PATH%"

REM ── THE RESOURCES, then the program ──────────────────────────────────────────────────────────────────────────────
IF NOT EXIST "%HERE%\build" MKDIR "%HERE%\build"
SET "RES=%HERE%\build\launcher.res"
"%WINDRES%" -I "%ROOT%" -O coff -F pe-x86-64 -o "%RES%" "%HERE%\launcher.rc"
IF ERRORLEVEL 1 (
  ECHO   C LAUNCHER BUILD FAILED: windres refused "%HERE%\launcher.rc".
  EXIT /B 1
)

REM The output folder may be a fresh publish folder, so create it rather than letting gcc fail on a missing path.
FOR %%O IN ("%OUT%") DO IF NOT EXIST "%%~dpO" MKDIR "%%~dpO"

REM -municode selects the wide entry point (wmain), which is what this program needs: a path or a script argument can hold
REM any character, and the narrow `main` would hand them over in the console's code page. -mconsole states the subsystem
REM (console, 3), which is what `tools/slc-pack.slc --console` keeps and what `--gui` flips to 2 in a COPY. -static and
REM -static-libgcc keep libgcc and libwinpthread OUT of the deployment: the launcher must be one file beside the DLL.
REM
REM THE SIZE FLAGS THAT ARE FREE, measured one set at a time on this box: -ffunction-sections/-fdata-sections with
REM -Wl,--gc-sections drop what nothing calls, and -fno-ident/-Wl,--build-id=none drop the producer comment and a build id
REM nothing reads. MEASURED 60,416 -> 57,856 B (-2,560, -4.2%).
REM
REM AND NO C RUNTIME AT ALL: -nostartfiles -nostdlib with -e naming the launcher's own wmainCRTStartup, so the image links
REM only kernel32 and shell32 (CommandLineToArgvW). MEASURED 57,856 -> 18,944 B (-67%), and launcher.c carries the small
REM section of allocator and string helpers that used to be libc. Two of them could NOT be redefined under their libc
REM names (mingw makes iswspace a MACRO and declares _wcsicmp dllimport), which is why the call sites are renamed.
REM
REM -fno-builtin IS REQUIRED HERE, not a size flag: without it gcc RECOGNISES the hand-written loops as the libc functions
REM they replace and emits calls to them anyway, so the link fails on strlen (measured: the first CRT-free build did exactly
REM that, from buffer_add, line_sink and wmain).
REM
REM LEFT OUT, each measured rather than assumed: -flto and -fno-unwind-tables changed NOTHING here (one translation unit,
REM and the unwind tables survive the link either way), and -Wl,--disable-dynamicbase with --disable-reloc-section buys
REM only 512 B while turning ASLR OFF for the launcher AND for every app packaged out of it, which no size win of half a
REM kilobyte can buy.
"%GCC%" -std=c11 -Oz -fno-stack-protector -fomit-frame-pointer -mno-stack-arg-probe -falign-functions=1 -falign-jumps=1 -falign-loops=1 -falign-labels=1 -fno-plt -s -Wall -Wextra -Werror -fno-builtin -mconsole -ffunction-sections -fno-asynchronous-unwind-tables -fno-unwind-tables -fdata-sections -fno-ident -Wl,--gc-sections -Wl,--build-id=none -Wl,--no-insert-timestamp -nostartfiles -nostdlib -e wmainCRTStartup -o "%OUT%" "%HERE%\launcher.c" "%RES%" -lkernel32 -lshell32
IF ERRORLEVEL 1 (
  ECHO   C LAUNCHER BUILD FAILED: gcc refused "%HERE%\launcher.c".
  EXIT /B 1
)

REM ── WHAT WAS PRODUCED, read out of the file rather than assumed ──────────────────────────────────────────────────
REM A build that says nothing about its artifact is how a 32-bit image or a GUI one reaches a shipping folder: the two
REM facts that decide whether it can run as this product's launcher are the MACHINE (x64 only, the DLL is win-x64) and the
REM SUBSYSTEM (console, so an unpacked launcher opened by a double click keeps its window and shows its refusals).
powershell -NoProfile -Command ^
  "$p='%OUT%'; if (-not (Test-Path $p)) { Write-Host 'BUILD CHECK FAILED: the build produced no file'; exit 1 };" ^
  "$b=[IO.File]::ReadAllBytes($p); $pe=[BitConverter]::ToInt32($b,0x3C);" ^
  "$m=[BitConverter]::ToUInt16($b,$pe+4); $s=$b[$pe+0x5C];" ^
  "Write-Host ('  {0}  {1:N0} B  machine=0x{2:X4}  subsystem={3}' -f (Split-Path -Leaf $p), $b.Length, $m, $s);" ^
  "if ($m -ne 0x8664) { Write-Host ('BUILD CHECK FAILED: machine 0x{0:X4} is not x64, so it cannot load the win-x64 engine' -f $m); exit 1 };" ^
  "if ($s -ne 3) { Write-Host ('BUILD CHECK FAILED: subsystem {0} is not console (3)' -f $s); exit 1 }"
IF ERRORLEVEL 1 EXIT /B 1

EXIT /B 0

REM ── A COMPILER IS TAKEN ONLY WHEN IT REALLY TARGETS x86_64 ────────────────────────────────────────────────────
REM :TargetOf %1 asks the compiler itself; :TryGcc accepts it only if that answer says x86_64, and otherwise records what
REM it said so the refusal can name it. `-dumpmachine` is answered by the driver without starting `cc1`, so this works
REM even when the toolchain's folder is not on PATH yet (which is the other trap this file documents).
:TargetOf
SET "TARGET="
SET "PROBE=%TEMP%\slc_launcher_gcc_probe.txt"
"%~1" -dumpmachine > "%PROBE%" 2>nul
FOR /F "delims=" %%M IN ('type "%PROBE%" 2^>nul') DO SET "TARGET=%%M"
DEL "%PROBE%" >nul 2>nul
GOTO :EOF

:TryGcc
CALL :TargetOf "%~1"
ECHO %TARGET% | findstr /C:"x86_64" >nul
IF ERRORLEVEL 1 (
  SET "REJECTED=%~1 (it targets %TARGET%)"
  GOTO :EOF
)
SET "GCC=%~1"
GOTO :EOF

:no_compiler
ECHO   C LAUNCHER BUILD FAILED: no x86_64 GCC was found, and a 32-bit one cannot load the engine's win-x64 DLL.
IF DEFINED REJECTED ECHO   Rejected: %REJECTED%
ECHO.
ECHO   Looked for gcc.exe in: %%SLC_MINGW_BIN%%, %%ProgramFiles%%\msys64\mingw64\bin, C:\msys64\mingw64\bin, and on PATH.
FOR /F "delims=" %%G IN ('where gcc 2^>nul') DO ECHO     on PATH: %%G
ECHO.
ECHO   Install the 64-bit toolchain, then run this again:
ECHO     C:\msys64\usr\bin\bash.exe -lc "pacman -S --needed mingw-w64-x86_64-gcc"
ECHO   Or point SLC_MINGW_BIN at a folder that holds gcc.exe and windres.exe.
EXIT /B 1

REM ── AN OVERRIDE THAT IS NOT USABLE SAYS SO, and nothing else is tried ────────────────────────────────────────────
:bad_override
ECHO   C LAUNCHER BUILD FAILED: SLC_MINGW_BIN names "%SLC_MINGW_BIN%", and that is not a usable x86_64 toolchain.
IF DEFINED REJECTED ECHO   Rejected: %REJECTED%
IF NOT EXIST "%SLC_MINGW_BIN%\gcc.exe" ECHO   There is no gcc.exe in that folder.
ECHO.
ECHO   Nothing else was tried on purpose: a declared folder is OBEYED, not consulted, so a typo cannot silently build with
ECHO   a different compiler than the one you named. Fix or clear SLC_MINGW_BIN, then run this again.
ECHO     C:\msys64\usr\bin\bash.exe -lc "pacman -S --needed mingw-w64-x86_64-gcc"
EXIT /B 1
