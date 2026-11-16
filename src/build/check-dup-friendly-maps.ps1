# src/build/check-dup-friendly-maps.ps1, a faithful port of src/build/check-dup-friendly-maps.sh for a box without bash.
#
# The authority stays the shell script: this port DELEGATES to it whenever bash exists (unless -Native), so on a box
# that has bash there is one implementation and no drift. It reads the SAME sources (src/**/*.cs) and reports with
# the shell script's own wording.
#
# What it proves, in the shell script's words: src/Interpreter/TypeHelpers.cs holds the ONLY switch tail that
# forwards a raw exception Message, no exception type is mapped to a string anywhere outside it, and no raw message
# is interpolated into a script-visible error. Only the copy a given input happens to reach matters, so a duplicate
# missing the newest case stays invisible until it leaks a raw CLR/Win32 message to a script, which is how
# "DllNotFound_Windows, sqlite3, <localized>" and "Arg_OutOfMemoryException" reached one.
#
# Two things the port must not soften, both the shell script's own rules: the searches are case-SENSITIVE (grep -E
# is, so a PowerShell -match, which is not, would flag a lower-case ".message" the shell script accepts), and only
# the INTERPOLATION form of check 3 is flagged, since passing a ScriptException's own (already friendly) message
# straight through is legitimate.
#
# The em dash the shell script writes in its messages is built as $em rather than typed here, and no other
# non-ASCII appears below line 1: Windows PowerShell 5.1 reads a BOM-less .ps1 in the ANSI code page, so a literal
# em dash (E2 80 94) decodes to three characters of which the last is a smart quote, and the string it sits in stops
# parsing. check-opcode-coverage.ps1 keeps its own em dash on line 1 for the same reason.
#
# Exit codes, as in the shell script: 0 clean, 1 duplicates found (each printed as file:line:...), 2 not run from
# the repository root.
param([switch]$Native, [switch]$Quiet)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $root
$em = [char]0x2014

if (-not $Native) {
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if ($bash) {
        Write-Host "delegating to the authority: $($bash.Source) src/build/check-dup-friendly-maps.sh   (use -Native for this port)"
        & $bash.Source "src/build/check-dup-friendly-maps.sh"
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path "src")) { [Console]::Error.WriteLine("no src/ here $em run this from the repository root"); exit 2 }
$canon = "src/Interpreter/TypeHelpers.cs"
$canonPrefix = $canon + ":"
$fail = 0

# `grep -rnE <pattern> src/ --include=*.cs`, rendered as file:line:text with forward-slash relative paths. -cmatch
# keeps the search case-sensitive, as grep -E is. Two details follow grep rather than PowerShell: -Force descends
# into hidden directories (grep -r does), and an Extension test replaces -Filter, which also matches 8.3 SHORT names
# and so can return a file that does not end in .cs at all. Files are visited in path order, so with several matches
# the listed lines may come in a different order than grep's; the verdict cannot.
function Find-InSources([string]$pattern) {
    $srcRoot = (Resolve-Path "src").Path
    $out = New-Object System.Collections.Generic.List[string]
    $files = Get-ChildItem -Path $srcRoot -Recurse -File -Force | Where-Object { $_.Extension -eq '.cs' }
    foreach ($f in ($files | Sort-Object FullName)) {
        $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
        $n = 0
        foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
            $n++
            if ($line -cmatch $pattern) { $out.Add("$rel`:$n`:$line") }
        }
    }
    return $out.ToArray()
}
function Test-Canon([string]$line) { return $line.StartsWith($canonPrefix, [System.StringComparison]::Ordinal) }
function Write-Indented([string[]]$lines) { foreach ($l in $lines) { [Console]::Error.WriteLine("  $l") } }

# 1. raw-message switch tails (`_ => ex.Message`, `_ => e.Message`, and so on)
$tails = @(Find-InSources '_ *=> *[A-Za-z_][A-Za-z0-9_]*\.Message')
if ($tails.Count -gt 1) {
    [Console]::Error.WriteLine("FAIL: $($tails.Count) switch tails forward a raw exception Message $em the friendly map must own the only one:")
    Write-Indented $tails
    $fail = 1
} elseif ($tails.Count -eq 1 -and -not (Test-Canon $tails[0])) {
    [Console]::Error.WriteLine("FAIL: the single raw-message tail is NOT in $canon $em a copy has replaced the canonical map:")
    Write-Indented $tails
    $fail = 1
}

# 2. arms that belong to the canonical map only
$copiedPattern = '(FileNotFound|DirectoryNotFound|UnauthorizedAccess|DivideByZero|Overflow|Format|NotImplemented|ArgumentOutOfRange|EntryPointNotFound|BadImageFormat)Exception[^;]*=>'
$copied = @(Find-InSources $copiedPattern | Where-Object { -not (Test-Canon $_) })
if ($copied.Count -gt 0) {
    [Console]::Error.WriteLine("FAIL: a copy of the friendly map $em an exception type mapped to a string belongs to $canon only:")
    Write-Indented $copied
    $fail = 1
}

# 3. raw message interpolated into a script-visible error
$bypassPattern = '(ThrowException|RuntimeError|RuntimeErrorAt|ScriptErrorInfo|ErrorInfo)\(.*\$"[^"]*\{[A-Za-z_][A-Za-z0-9_.]*\.Message'
$bypass = @(Find-InSources $bypassPattern | Where-Object { -not (Test-Canon $_) })
if ($bypass.Count -gt 0) {
    [Console]::Error.WriteLine("FAIL: a raw exception Message is interpolated into a script-visible error:")
    Write-Indented $bypass
    $fail = 1
}

if ($fail -eq 0) {
    Write-Host "ok: one friendly exception map ($canon), no copies, no interpolated raw messages"
    exit 0
}
[Console]::Error.WriteLine("")
[Console]::Error.WriteLine("Fix: have the offending site call FriendlyExceptionMessage(ex) instead of mapping messages itself.")
exit 1
