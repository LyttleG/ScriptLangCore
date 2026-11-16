@echo off
rem -- tools\cdb-gc-leak.cmd ---------------------------------------------------------------------------------------
rem Diagnose ScriptLangCore.exe under cdb. Two modes:
rem
rem   (default)   drive ".gc leak" in the REPL and RETRY: the crash this exists for is intermittent, so one clean
rem               attempt proves nothing. Every attempt is appended to an archive; the fault check reads only the
rem               attempt just run.
rem   -handles    name the handle TYPE that leaks: run assets\language\probes\cdb-handles-probe.slc (3 Excel activations, with a
rem               DebugBreak before and after) and dump the handle table at three stops so the delta is visible.
rem
rem usage:  tools\cdb-gc-leak.cmd [-handles] [script] [iterations] [attempts] [exe]
rem   script      default: ..\assets\language\com\sqlite_excel_csv_test.slc   (-handles ignores it)
rem   iterations  default: 50     attempts default: 5     exe default: ..\publish-aot\ScriptLangCore.exe
rem
rem cdb's path: the Store package's version directory changes per release and a GLOB DOES NOT WORK there (WindowsApps
rem denies directory listing) -- paste your literal path below if neither candidate is right.
setlocal
set "MODE=leak"
if /i "%~1"=="-handles" set "MODE=handles"
if /i "%MODE%"=="handles" shift
set "REPO=%~dp0.."
set "SCRIPT=%~1"
if "%SCRIPT%"=="" set "SCRIPT=%REPO%\assets\language\com\sqlite_excel_csv_test.slc"
set "ITERS=%~2"
if "%ITERS%"=="" set "ITERS=50"
set "ATTEMPTS=%~3"
if "%ATTEMPTS%"=="" set "ATTEMPTS=5"
set "EXE=%~4"
if "%EXE%"=="" set "EXE=%REPO%\publish-aot\ScriptLangCore.exe"
if not exist "%EXE%" set "EXE=%REPO%\bin\Release\net11.0\win-x64\publish\ScriptLangCore.exe"

set "CDB=%ProgramFiles%\WindowsApps\Microsoft.WinDbg_1.2606.22001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe"
if not exist "%CDB%" set "CDB="
if not defined CDB for /f "delims=" %%C in ('where cdb 2^>nul') do if not defined CDB set "CDB=%%C"

set "LOG=%TEMP%\cdb-gc-leak.log"
set "ARCH=%TEMP%\cdb-gc-leak-all.log"
set "DMP=%TEMP%\cdb-gc-leak-crash.dmp"
set "CMDS=%TEMP%\cdb-gc-leak.cmds"

if not exist "%EXE%" echo ERROR: exe not found: "%EXE%"   ^(build it: dotnet publish -c Release^)& exit /b 2
if not defined CDB echo ERROR: cdb.exe not found -- edit the CDB= line in this file. & exit /b 2

echo script    : %SCRIPT%
echo exe       : %EXE%
echo cdb       : %CDB%
echo current   : %LOG%    ^(the attempt just run^)
echo archive   : %ARCH%    ^(EVERY attempt, appended -- read this one^)
echo.

if /i "%MODE%"=="handles" goto :handles

echo mode      : .gc leak x %ITERS%, up to %ATTEMPTS% attempt^(s^)
> "%CMDS%" echo .gc leak %SCRIPT% %ITERS%
>>"%CMDS%" echo .exit
del "%DMP%" 2>nul
del "%ARCH%" 2>nul
for /L %%A in (1,1,%ATTEMPTS%) do (
  echo === attempt %%A of %ATTEMPTS% ===
  "%CDB%" -o -g -G -c "g; .dump /ma %DMP%; !analyze -v; ~*kvn; q" "%EXE%" < "%CMDS%" > "%LOG%"
  echo. >> "%ARCH%"
  echo === attempt %%A of %ATTEMPTS% ^(mode: .gc leak x %ITERS%^) === >> "%ARCH%"
  type "%LOG%" >> "%ARCH%"
  findstr /C:"Access violation" /C:"c0000005" "%LOG%" >nul
  if not errorlevel 1 (
    echo   FAULTED -- last progress line, then what faulted:
    findstr /C:"Progress:" /C:"ExceptionAddress" /C:"Value:" /C:"AV.Type" "%LOG%"
    echo   all tries: %ARCH%
    if exist "%DMP%" echo   dump     : %DMP%   ^(open in WinDbg for the frame and source line^)
    goto :done
  )
  echo   survived attempt %%A
)
echo No fault in %ATTEMPTS% attempt^(s^): it is intermittent -- raise the attempt count or the iteration count.
goto :done

:handles
rem NO -g here, deliberately: with -g cdb runs to the script's FIRST DebugBreak before executing the -c commands, so
rem every dump lands at the second break and the baseline is never captured (measured: two identical tables). The
rem probe stops itself; !handle 0 0 is the form that prints "Type <name>" plus the machine's Type/Count summary.
set "PROBE=%REPO%\assets\language\probes\cdb-handles-probe.slc"
if not exist "%PROBE%" echo ERROR: probe not found: "%PROBE%" & exit /b 2
echo mode      : -handles   ^(3 Excel activations with a DebugBreak before and after^)
"%CDB%" -o -G -c "!handle 0 0; g; !handle 0 0; g; !handle 0 0; g; !handle 0 0; q" "%EXE%" "%PROBE%" > "%LOG%"
copy /y "%LOG%" "%ARCH%" >nul 2>nul
echo cdb exit=%ERRORLEVEL%
echo --- handle lines and the Type/Count tables, in file order ---
findstr /N /C:"Type " /C:"Handles" "%LOG%"
echo.
echo Stops: 1 = INITIAL, 2 = before the Excels, 3 = after them, 4 = after the probe's 4-minute idle.
echo 3 = AFTER them. The delta from 1 to 3 is the leak: one ALPC Port per out-of-process activation, the RPC/COM
echo connection to that server -- the runtime's, not something our Release calls can free.
echo archive: %ARCH%
:done
endlocal
