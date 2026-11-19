@ECHO OFF
REM THE NATIVE PLUGIN FIXTURE'S BUILD: jitplugin_native.c through an x86_64 MinGW-w64 GCC, into one DLL the engine
REM loads with LoadLibrary. Counterpart of `src\embed\launcher-c\build.bat`, and the same two traps apply.
REM
REM USAGE: build.bat [output.dll]
REM   no argument        src\jitplugins\native\bin\JitPluginNative.dll
REM   an argument        exactly that path (src\build\jitplugin-check.sh passes its own staging folder)
REM
REM THIS FILE IS THE ONE STATEMENT OF THE COMPILE LINE. `build.sh` beside it is a thin wrapper that converts its
REM arguments for cmd and calls this file, so the flags cannot drift between a shell and a prompt.
REM
REM AN x86_64 COMPILER IS REQUIRED, and half this file is that check: the `gcc` first on PATH on this box targets
REM i686-w64-mingw32, and a 32-bit DLL cannot be loaded by the 64-bit engine at all (Windows answers "the specified
REM module could not be found" for an architecture mismatch, which reads like a missing file). The toolchain is
REM therefore probed and REFUSED BY NAME rather than taken from PATH blindly.
REM
REM WHERE IT LOOKS, in order: SLC_MINGW_BIN (a directory override) -> %ProgramFiles%\msys64\mingw64\bin ->
REM C:\msys64\mingw64\bin -> each `gcc` on PATH whose `-dumpmachine` says x86_64.

SETLOCAL ENABLEEXTENSIONS

SET "HERE=%~dp0"
IF "%HERE:~-1%"=="\" SET "HERE=%HERE:~0,-1%"
SET "OUT=%~1"
IF NOT DEFINED OUT SET "OUT=%HERE%\bin\JitPluginNative.dll"
SET "SRC=%HERE%\jitplugin_native.c"
IF NOT EXIST "%SRC%" (
  ECHO   NATIVE PLUGIN BUILD FAILED: "%SRC%" is not there.
  EXIT /B 1
)

REM -- THE TOOLCHAIN -------------------------------------------------------------------------------------------------
REM EVERY candidate goes through :TryGcc, including the explicit override: the check that matters is what the compiler
REM SAYS it targets, not where it was found. An override is OBEYED, not consulted, so a typo cannot silently build with
REM another compiler than the one named.
SET "GCC="
SET "REJECTED="
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
SET "OBJDUMP=%BINDIR%objdump.exe"
IF NOT EXIST "%OBJDUMP%" (
  ECHO   NATIVE PLUGIN BUILD FAILED: "%GCC%" was found but there is no objdump.exe beside it, and the EXPORT has to be
  ECHO   read back out of the artifact: a DLL without "PluginInit" is a plugin the host can never find.
  ECHO   Install the whole toolchain: pacman -S mingw-w64-x86_64-gcc
  EXIT /B 1
)

REM THE TOOLCHAIN'S OWN FOLDER GOES ON PATH, and it is not tidiness: `gcc.exe` starts `cc1.exe` from its lib\gcc folder,
REM and that program needs libwinpthread, libisl, libmpc, libmpfr, libgmp and libzstd FROM THE SAME BIN. Measured: without
REM it, `gcc -c hello.c` answers exit code 1 and NOTHING AT ALL, which reads like a compiler refusing valid source.
SET "PATH=%BINDIR%;%PATH%"

REM The output folder may be a fresh staging folder, so create it rather than letting gcc fail on a missing path.
FOR %%O IN ("%OUT%") DO IF NOT EXIST "%%~dpO" MKDIR "%%~dpO"

REM -shared, because a plugin is a DLL. -static-libgcc keeps libgcc OUT of the deployment, so the plugin is one file.
REM -Wl,--no-insert-timestamp makes two builds of the same source byte-identical. The contract's own rules (UTF-8, the
REM snprintf length convention, no exception across an entry point) live in the source's header, not here.
REM "%GCC%" -shared -O2 -Wall -Wextra -Werror -static-libgcc -Wl,--no-insert-timestamp -o "%OUT%" "%SRC%"
"%GCC%" -shared -Os -s -Wall -Wextra -Werror ^
  -ffunction-sections -fdata-sections -fno-unwind-tables -fno-asynchronous-unwind-tables ^
  -fno-ident -fvisibility=hidden -fno-stack-protector ^
  -Wl,--gc-sections -Wl,--no-insert-timestamp -Wl,--build-id=none ^
  -o "%OUT%" "%SRC%"

IF ERRORLEVEL 1 (
  ECHO   NATIVE PLUGIN BUILD FAILED: gcc refused "%SRC%".
  EXIT /B 1
)

REM -- WHAT WAS PRODUCED, read out of the file rather than assumed --------------------------------------------------
REM The export IS the contract: the host resolves the table by `GetProcAddress("PluginInit")`, so a decorated or absent
REM name is a plugin that loads and then answers nothing. objdump comes with the toolchain, so this is always available
REM once the compiler above was found.
SET "EXPORTS=%TEMP%\slc_native_plugin_exports.txt"
"%OBJDUMP%" -p "%OUT%" > "%EXPORTS%" 2>nul
findstr /C:"PluginInit" "%EXPORTS%" >nul
IF ERRORLEVEL 1 (
  ECHO   NATIVE PLUGIN BUILD FAILED: "%OUT%" exports no PluginInit, and the host resolves the table by that name.
  DEL "%EXPORTS%" >nul 2>nul
  EXIT /B 1
)
DEL "%EXPORTS%" >nul 2>nul

FOR %%F IN ("%OUT%") DO ECHO   %%~nxF  %%~zF B  exports PluginInit

EXIT /B 0

REM -- A COMPILER IS TAKEN ONLY WHEN IT REALLY TARGETS x86_64 ----------------------------------------------------
REM `-dumpmachine` is answered by the driver without starting `cc1`, so this works even when the toolchain's folder is
REM not on PATH yet (which is the other trap this file documents).
:TargetOf
SET "TARGET="
SET "PROBE=%TEMP%\slc_native_plugin_gcc_probe.txt"
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
ECHO   NATIVE PLUGIN BUILD FAILED: no x86_64 GCC was found, and a 32-bit one cannot produce a DLL this engine loads.
IF DEFINED REJECTED ECHO   Rejected: %REJECTED%
ECHO.
ECHO   Looked for gcc.exe in: %%SLC_MINGW_BIN%%, %%ProgramFiles%%\msys64\mingw64\bin, C:\msys64\mingw64\bin, and on PATH.
FOR /F "delims=" %%G IN ('where gcc 2^>nul') DO ECHO     on PATH: %%G
ECHO.
ECHO   Install the 64-bit toolchain, then run this again:
ECHO     C:\msys64\usr\bin\bash.exe -lc "pacman -S --needed mingw-w64-x86_64-gcc"
ECHO   Or point SLC_MINGW_BIN at a folder that holds gcc.exe and objdump.exe.
EXIT /B 1

REM -- AN OVERRIDE THAT IS NOT USABLE SAYS SO, and nothing else is tried --------------------------------------------
:bad_override
ECHO   NATIVE PLUGIN BUILD FAILED: SLC_MINGW_BIN names "%SLC_MINGW_BIN%", and that is not a usable x86_64 toolchain.
IF DEFINED REJECTED ECHO   Rejected: %REJECTED%
IF NOT EXIST "%SLC_MINGW_BIN%\gcc.exe" ECHO   There is no gcc.exe in that folder.
ECHO.
ECHO   Nothing else was tried on purpose: a declared folder is OBEYED, not consulted, so a typo cannot silently build
ECHO   with a different compiler than the one you named. Fix or clear SLC_MINGW_BIN, then run this again.
ECHO     C:\msys64\usr\bin\bash.exe -lc "pacman -S --needed mingw-w64-x86_64-gcc"
EXIT /B 1
