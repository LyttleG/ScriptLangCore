# src/build/check-opcode-coverage.ps1, a faithful port of src/build/check-opcode-coverage.sh for a box without bash.
#
# The authority stays the shell script: this port DELEGATES to it whenever bash exists (unless -Native), so on a box
# that has bash there is one implementation and no drift. It reads the SAME sources and the SAME registry
# (src/build/opcode-not-emitted.txt), and reports with the shell script's own wording.
#
# What it proves, in the shell script's words: every opcode is handled by the executor, by one of the disassembler's
# two tables, and by the format table; the format table declares no opcode the enum lacks; every opcode the format
# table says carries an operand is decoded by the operands table; the verifier's stack-effect table covers the enum;
# and the only opcodes with no emission site are the ones the registry names. It does NOT prove operand SIZES agree:
# that is src/build/dump-bytecode.sh's job (emitted bytes) and the disassembler decoding real streams.
#
# The scoping below is not decoration. The shell script records a MEASURED trap: reading the whole of Vm.cs let the
# Arith helper's arms satisfy the disassembler check for an opcode the disassembler could no longer render, so each
# table is read out of its OWN switch body.
param([switch]$Native, [switch]$Quiet)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $root

if (-not $Native) {
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if ($bash) {
        Write-Host "delegating to the authority: $($bash.Source) src/build/check-opcode-coverage.sh   (use -Native for this port)"
        & $bash.Source "src/build/check-opcode-coverage.sh"
        exit $LASTEXITCODE
    }
}
$fail = 0
$vmPath = "src/Interpreter/Vm.cs"; $fmtPath = "src/Interpreter/BytecodeFormat.cs"
$enumPath = "src/Interpreter/Opcodes.cs"; $verPath = "src/Interpreter/BytecodeVerifier.cs"

function Get-Block([string]$path, [string]$startText, [string]$endPattern) {
    $text = [System.IO.File]::ReadAllText($path)
    $i = $text.IndexOf($startText)
    if ($i -lt 0) { return @() }
    $rest = $text.Substring($i)
    $end = [regex]::Match($rest, $endPattern)
    if (-not $end.Success) { return @() }
    return ($rest.Substring(0, $end.Index + $end.Length) -split "\r?\n")
}
function Strip-Comments([string[]]$lines) { return ($lines | ForEach-Object { $_ -replace '//.*', '' }) }
function Names-In([string[]]$lines) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) {
        foreach ($m in [regex]::Matches($l, 'Opcode\.([A-Z0-9_]+)')) { if (-not $out.Contains($m.Groups[1].Value)) { $out.Add($m.Groups[1].Value) } }
    }
    return $out.ToArray()
}
# The enum's members are BARE names (ADD, CALL_VAL, ...), so they need their own extractor: the shell script splits
# the block on commas and takes the leading name of each part. Every other table writes `Opcode.<NAME>`.
function Enum-Names([string[]]$lines) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) {
        foreach ($part in ($l -split ',')) {
            $m = [regex]::Match($part, '^\s*([A-Z][A-Z0-9_]*)')
            if ($m.Success -and -not $out.Contains($m.Groups[1].Value)) { $out.Add($m.Groups[1].Value) }
        }
    }
    return $out.ToArray()
}
function MissingFrom([string[]]$have, [string[]]$need) { return @($need | Where-Object { $have -notcontains $_ } | Sort-Object) }
function Report([string]$label, [string[]]$have, [string[]]$need) {
    $missing = MissingFrom $have $need
    if ($missing.Count -gt 0) {
        Write-Host "FAIL: $label does not handle:"
        $missing | ForEach-Object { Write-Host "  $_" }
        $script:fail = 1
    }
}

# 1. the enum is the definition. 2. the executor's cases, read from the whole file (case labels are unambiguous).
$enum = Enum-Names (Strip-Comments @(Get-Block $enumPath "internal enum Opcode" "(?m)^}"))
if ($enum.Count -eq 0) { Write-Host "could not read the Opcode enum out of $enumPath"; exit 1 }
$exec = Names-In @([System.IO.File]::ReadAllLines($vmPath) | Where-Object { $_ -match 'case Opcode\.' })

# 3. the disassembler's two tables, each out of its OWN switch body.
    $nameTab = Names-In (Strip-Comments (Get-Block $vmPath "private static string OpcodeMeaning(Opcode op) => op switch" "(?m)^[ \t]*\};"))
    $operandTab = Names-In (Strip-Comments (Get-Block $vmPath "var operandDecode = op switch" "(?m)^[ \t]*\};"))
if ($nameTab.Count -eq 0 -or $operandTab.Count -eq 0) { Write-Host "could not read the disassembler's tables out of $vmPath"; exit 1 }

# The two tables partition the opcode set, so each alone is partial, but together they must be total, and an opcode
# named by BOTH means one of the two renderings is lying about whether it has operands.
$both = @($nameTab | Where-Object { $operandTab -contains $_ } | Sort-Object)
if ($both.Count -gt 0) {
    Write-Host "FAIL: the disassembler renders these opcodes in BOTH its tables (one rendering is stale):"
    $both | ForEach-Object { Write-Host "  $_" }
    $fail = 1
}
$dis = @(($nameTab + $operandTab) | Sort-Object -Unique)

# 4. the format table, and WHICH kind each opcode got (arms may group opcodes and wrap over lines).
    $fmtLines = Strip-Comments (Get-Block $fmtPath "Kind(Opcode op) => op switch" "(?m)^[ \t]*\};")
$fmt = Names-In $fmtLines
$withOperand = New-Object System.Collections.Generic.List[string]
$pend = New-Object System.Collections.Generic.List[string]
foreach ($l in $fmtLines) {
    foreach ($m in [regex]::Matches($l, 'Opcode\.([A-Z0-9_]+)')) { $pend.Add($m.Groups[1].Value) }
    $k = [regex]::Match($l, '=>[ \t]*OperandKind\.([A-Za-z0-9_]+)')
    if ($k.Success) {
        if ($k.Groups[1].Value -ne "None") { foreach ($p in $pend) { if (-not $withOperand.Contains($p)) { $withOperand.Add($p) } } }
        $pend = New-Object System.Collections.Generic.List[string]
    }
}
$withOperand = @($withOperand | Sort-Object)
if ($withOperand.Count -eq 0) { Write-Host "could not read the operand kinds out of $fmtPath"; exit 1 }

# 4c. the verifier's stack-effect table: its `_ => (0, 0)` default reads a MISSING arm as "no effect at all", which
# is a wrong answer rather than a crash, so nothing else would notice a table that stopped covering the enum.
    $effect = Names-In (Strip-Comments (Get-Block $verPath "Effect(Opcode op, byte[] code, int operandBase)" "(?m)^[ \t]*\};"))

Report "the executor" $exec $enum
Report "the disassembler" $dis $enum
Report "BytecodeFormat" $fmt $enum
Report "the verifier's stack-effect table" $effect $enum
Report "the disassembler's operand table (every opcode BytecodeFormat says carries an operand)" $operandTab $withOperand
$extra = MissingFrom $enum $fmt
if ($extra.Count -gt 0) { Write-Host "FAIL: BytecodeFormat declares opcodes the enum does not have:"; $extra | ForEach-Object { Write-Host "  $_" }; $fail = 1 }

# 5. reachability, two-way against the registry.
$emitLines = @([System.IO.File]::ReadAllLines("src/Interpreter/Compiler.cs") | Where-Object { $_ -notmatch 'case Opcode\.' })
$emits = Names-In $emitLines
$retired = @()
foreach ($l in [System.IO.File]::ReadAllLines("src/build/opcode-not-emitted.txt")) {
    $t = ($l -split '#')[0].Trim()
    if ($t.Length -gt 0) { $retired += ($t -split '\s+')[0] }
}
$retired = @($retired | Sort-Object -Unique)
$noEmit = MissingFrom $emits $enum
$unlisted = @($noEmit | Where-Object { $retired -notcontains $_ })
if ($unlisted.Count -gt 0) { Write-Host "FAIL: no emission site in Compiler.cs, and not listed in src/build/opcode-not-emitted.txt:"; $unlisted | ForEach-Object { Write-Host "  $_" }; $fail = 1 }
$stale = @($retired | Where-Object { $noEmit -notcontains $_ })
if ($stale.Count -gt 0) { Write-Host "FAIL: listed as never emitted, but Compiler.cs now emits these:"; $stale | ForEach-Object { Write-Host "  $_" }; $fail = 1 }

if ($fail -eq 0) {
    Write-Host "ok: all $($enum.Count) opcodes are known to the executor, the disassembler, the format table and the verifier's stack-effect table; the operand table decodes all $($withOperand.Count) opcodes the format table gives an operand; and the only opcodes with no emission site are the $($retired.Count) that src/build/opcode-not-emitted.txt names"
}
exit $fail
