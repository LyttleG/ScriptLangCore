# src/build/run-repl-tests.ps1, the REPL/CLI test gate for a Windows box without bash.
#
# Why this exists: src/build/run-repl-tests.sh is the authority and stays the authority. This port exists because a
# maintainer's box may have no bash at all, and an unrun gate is worse than a second implementation. So it is
# written to be FAITHFUL rather than opinionated:
#   * it reads the SAME registries (src/build/repl-test-skip.txt, repl-test-known-fail.txt, repl-test-env.txt), so the
#     data lives in one place and only the loop is duplicated;
#   * it DELEGATES to the bash gate when bash IS available, unless -Native is given, so on a box that has bash there
#     is one implementation and no drift;
#   * it decides pass/fail the way the bash gate does, including the part that matters most: many suites exit 0 even
#     with failing checks, so the "N passed, M failed" SUMMARY LINE is authoritative, then a first [ERROR] line,
#     then "Failed: N", then "TEST(S) FAILED". Never a generic grep for "failed".
#
# What this port does NOT have, and the bash gate does: --build newest (the newest-of-the-two choice), --json, and
# --profile correctness with its slow-suite file. It has -Build aot|jit|both|aot-first, -Filter, -Exe, -Timeout,
# -AllowStale, -Native and -Quiet.
#
# Two traps it cost, both found by running it rather than reading it: Start-Process -PassThru does NOT report
# ExitCode (it came back EMPTY, so every passing suite looked like a failure, and the process is started through
# System.Diagnostics.Process instead, with both streams read asynchronously so a chatty suite cannot block on the
# redirect), and the working directory is the REPO ROOT rather than the exe's folder, because the suites read
# relative paths.
## Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File src\build\run-repl-tests.ps1 [-Build aot|jit|both] [-Filter NAME]
#            [-Exe PATH] [-Timeout SECONDS] [-AllowStale] [-Native] [-Quiet]
param(
    [string]$Build = "aot-first",
    [string]$Filter = "",
    [string]$Exe = "",
    [int]$Timeout = 20,
    [switch]$AllowStale,
    [switch]$Native,
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $root

if (-not $Native) {
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if ($bash) {
        $forward = @("src/build/run-repl-tests.sh")
        if ($Build) { $forward += @("--build", $Build) }
        if ($Filter) { $forward += @("--filter", $Filter) }
        if ($Exe) { $forward += @("--exe", $Exe) }
        if ($AllowStale) { $forward += "--allow-stale" }
        Write-Host "delegating to the authority: $($bash.Source) $($forward -join ' ')   (use -Native to run this port)"
        & $bash.Source @forward
        exit $LASTEXITCODE
    }
}

$suiteDir = Join-Path $root "assets/language/repl"
$skipFile = Join-Path $root "src/build/repl-test-skip.txt"
$knownFailFile = Join-Path $root "src/build/repl-test-known-fail.txt"
$envFile = Join-Path $root "src/build/repl-test-env.txt"
$aotBin = Join-Path $root "bin/Release/net11.0/win-x64/publish/ScriptLangCore.exe"
$jitBin = Join-Path $root "bin/Debug/net11.0/ScriptLangCore.exe"

function Read-Registry([string]$path) {
    $out = @()
    if (-not (Test-Path $path)) { return $out }
    foreach ($line in Get-Content $path) {
        $t = ($line -split '#')[0].Trim()
        if ($t.Length -gt 0) { $out += $t }
    }
    return $out
}

$suiteEnv = @{}
$suiteTimeout = @{}
foreach ($line in Read-Registry $envFile) {
    $parts = $line -split '\s+'
    $vname = $parts[0]
    if (-not $vname.EndsWith(".slc")) { continue }
    $vars = ""
    foreach ($p in $parts[1..($parts.Count - 1)]) {
        if ($p -match '^TIMEOUT=(\d+)$') { $suiteTimeout[$vname] = [int]$Matches[1] }
        elseif ($p -match '^[A-Za-z_][A-Za-z0-9_]*=') { $vars += " $p" }
    }
    if ($vars.Length -gt 0) { $suiteEnv[$vname] = $vars.Trim() }
}
$skipPatterns = Read-Registry $skipFile
$knownFail = Read-Registry $knownFailFile

$sourceNewest = (Get-ChildItem (Join-Path $root "src") -Recurse -Include *.cs,*.json |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1)
$picked = @()
if ($Exe) {
    if (-not (Test-Path $Exe)) { Write-Host "no such --exe: $Exe"; exit 2 }
    $picked += @{ Path = (Resolve-Path $Exe).Path; Platform = "custom" }
} else {
    $wanted = @()
    switch ($Build) {
        "jit" { $wanted = @("jit") }
        "aot" { $wanted = @("aot") }
        "both" { $wanted = @("jit", "aot") }
        default { if (Test-Path $aotBin) { $wanted = @("aot") } elseif (Test-Path $jitBin) { $wanted = @("jit") } }
    }
    foreach ($w in $wanted) {
        $bin = if ($w -eq "aot") { $aotBin } else { $jitBin }
        if (-not (Test-Path $bin)) {
            Write-Host "-- platform $w : skipped ($bin is missing)"
            if ($w -eq "aot") { Write-Host "   build it: compile_aot.bat   (dotnet publish -c Release -r win-x64 --self-contained)" }
            else { Write-Host "   build it: compile_jit.bat   (dotnet build -c Debug -v q --nologo)" }
            continue
        }
        if ($sourceNewest -and ((Get-Item $bin).LastWriteTime -lt $sourceNewest.LastWriteTime) -and -not $AllowStale) {
            Write-Host "REFUSING: the $w binary $bin is OLDER than $($sourceNewest.FullName)."
            Write-Host "  A stale binary would report OLD behaviour, so there is nothing trustworthy to gate."
            Write-Host "  Rebuild it, or to verify this exact artifact deliberately: -AllowStale"
            exit 2
        }
        $picked += @{ Path = $bin; Platform = $w }
    }
    if ($picked.Count -eq 0) { Write-Host "no runner build available"; exit 2 }
}

$pass = 0; $fail = 0; $skip = 0; $known = 0; $timedout = 0; $staleKnown = 0
$failedNames = @()
$knownNames = @()
$staleNames = @()
$suites = Get-ChildItem $suiteDir -Recurse -File -Filter *.slc | Sort-Object FullName
$ran = 0
foreach ($suite in $suites) {
    $name = $suite.Name
    if ($Filter -ne "" -and -not $name.Contains($Filter)) { continue }
    $ran++
    $isSkip = $false
    foreach ($pat in $skipPatterns) { if ($name -like $pat) { $isSkip = $true; break } }
    if ($isSkip) { $skip++; if (-not $Quiet) { Write-Host "skip         $name" }; continue }
    $isKnownFail = $knownFail -contains $name
    foreach ($entry in $picked) {
        $exePath = $entry.Path
        $platform = $entry.Platform
        $budget = if ($suiteTimeout.ContainsKey($name)) { $suiteTimeout[$name] } else { $Timeout }
          # The script is passed as an ABSOLUTE path resolved against the SOURCE tree, so what runs is the suite
          # under version control rather than the copy deployed beside the executable.
          $script = $suite.FullName
          $exeDir = Split-Path $exePath -Parent
          # The engine's DEPLOYED layout (assets/, tools/, sqlite3.dll) lives NEXT TO the executable, so a
          # suite runs from there: that is what lets a suite reach "./sqlite3". This mirrors the authority
          # (src/build/run-repl-tests.sh), which has run suites this way since 2026-09-10. This port kept the
          # repo root as the cwd instead, which is why the sqlite suites failed HERE while passing there.
          $workDir = if (Test-Path (Join-Path $exeDir 'assets')) { $exeDir } else { $root }
        $outFile = [System.IO.Path]::GetTempFileName()
        $errFile = [System.IO.Path]::GetTempFileName()
        $saved = @{}
        if ($suiteEnv.ContainsKey($name)) {
            foreach ($pair in ($suiteEnv[$name] -split '\s+')) {
                $kv = $pair -split '=', 2
                if ($kv.Count -eq 2) {
                    $saved[$kv[0]] = [Environment]::GetEnvironmentVariable($kv[0])
                    [Environment]::SetEnvironmentVariable($kv[0], $kv[1])
                }
            }
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exePath
        $psi.Arguments = '"' + $script + '"'
        $psi.WorkingDirectory = $workDir
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        # Both streams are read ASYNCHRONOUSLY before waiting (a suite printing more than a pipe buffer would
        # otherwise block on the redirect), and this API, unlike Start-Process -PassThru, reports ExitCode reliably:
        # with -PassThru the code came back EMPTY, which made every passing suite look like a failure.
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()
        $finished = $proc.WaitForExit($budget * 1000)
        if (-not $finished) { try { $proc.Kill($true) } catch { }; $proc.WaitForExit() }
        $text = $outTask.Result + "`n" + $errTask.Result
        $exitCode = $proc.ExitCode
        $sw.Stop()
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }

        $status = "PASS"; $detail = ""
        if (-not $finished) { $status = "TIMEOUT"; $detail = "exceeded ${budget}s"; $timedout++ }
        elseif ($exitCode -ne 0) {
            # A suite that DIED has no tally line at all, and "exit=1" alone reads like a failing check. The bash
            # authority names the difference for the same reason: an abort before the first Check prints no failing
            # line either, which is how a broken suite came to read as OK in a hand-rolled loop over this output.
            $status = "FAIL"
            $detail = if ($text -match '(\d+) passed, (\d+) failed') { "exit=$exitCode" }
                      else { "exit=$exitCode (died before any check: no tally line)" }
        }
        else {
            # The bash gate's ORDER, exactly: a first [ERROR] line, then the SUMMARY LINE (authoritative, because
            # many suites exit 0 even with failing checks), then "Failed: N", then "TEST(S) FAILED". Swapping the
            # first two would pass a suite that printed an [ERROR] but ended on a clean summary.
            $err = [regex]::Match($text, '\[ERROR\][^\r\n]*')
            if ($err.Success) { $status = "FAIL"; $detail = $err.Value.Substring(0, [Math]::Min(200, $err.Value.Length)) }
            else {
                $m = [regex]::Match($text, '(\d+) passed, (\d+) failed')
                if ($m.Success -and [int]$m.Groups[2].Value -gt 0) { $status = "FAIL"; $detail = $m.Value }
                else {
                    $f2 = [regex]::Match($text, 'Failed: (\d+)')
                    if ($f2.Success -and [int]$f2.Groups[1].Value -gt 0) { $status = "FAIL"; $detail = "Failed: $($f2.Groups[1].Value)" }
                    elseif ($text -match 'TEST\(S\) FAILED') { $status = "FAIL"; $detail = "TEST(S) FAILED" }
                }
            }
        }
        $label = if ($picked.Count -gt 1) { "$name [$platform]" } else { $name }
        if ($status -eq "PASS" -and $isKnownFail) { $status = "KNOWN-STALE"; $detail = "passes now: remove it from repl-test-known-fail.txt" }
        if ($status -eq "PASS") { $pass++ }
        elseif ($status -eq "KNOWN-STALE") { $staleKnown++; $staleNames += "$label :: $detail" }
        elseif ($isKnownFail) { $known++; $knownNames += "$label :: $status $detail" }
        else { $fail++; $failedNames += "$label :: $status $detail" }
        if (-not $Quiet) {
            $secs = "{0:0.0}s" -f $sw.Elapsed.TotalSeconds
            if ($status -eq "PASS") { Write-Host "pass         $label  ($secs)" }
            elseif ($status -eq "KNOWN") { Write-Host "known-fail   $label :: $detail" }
            else { Write-Host "$($status.ToLower())       $label :: $detail" }
        }
    }
}

Write-Host ""
if ($Filter -ne "") { Write-Host "filtered to $ran suite(s) matching '$Filter'" }
Write-Host "pass=$pass fail=$fail skip=$skip known-fail=$known known-stale=$staleKnown timeout=$timedout"
if ($failedNames.Count -gt 0) {
    Write-Host ""
    Write-Host "FAILED:"
    foreach ($f in $failedNames) { Write-Host "  $f" }
}
if ($staleNames.Count -gt 0) {
    Write-Host ""
    Write-Host "known-fail entries that PASS now (not a gate failure, but the entry is stale):"
    foreach ($z in $staleNames) { Write-Host "  $z" }
}
if ($knownNames.Count -gt 0) {
    Write-Host ""
    Write-Host "known failures (not a gate failure, still a failure):"
    foreach ($k in $knownNames) { Write-Host "  $k" }
}
if ($fail -gt 0 -or $timedout -gt 0) { exit 1 }
exit 0
