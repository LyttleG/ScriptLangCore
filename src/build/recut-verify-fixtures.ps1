# Re-cut the _verify_bad_*.bin fixtures after a BUNDLE FORMAT change, and print the sizes, md5s and
# absolute offsets that assets/language/repl/vm/vm_verify_refused.slc records in its provenance block.
#
# Why a script: the fixtures are real exports with one byte changed, so a format change shifts every body,
# and rebuilding them by hand is where mistakes creep in. It needs a Debug build, since it drives --compile.
#
# HOW A BODY IS LOCATED, and why not by the 3-byte signature: a function body in the tail begins with
# `32 01 00`, but that sequence is NOT unique inside brProbe's code (it also starts the nested `n > 0` and
# return sequences), so the provenance block's own recipe is followed instead: take the function's FULL code
# array from `--dump-bytecode <source>` and find THAT array in the blob. The previous version of this script
# located the body by the 3-byte signature and took the LAST hit as the body's start, which was already
# wrong for brProbe's layout (measured: the last hit is the body's byte 23, so the corruption landed 23 bytes
# too far along and the three jump fixtures answered an unrelated "Arithmetic overflow" instead of their
# rule). It also derived the corruption offsets from a checked-out v4 fixture, a recipe that goes stale the
# moment HEAD stops being that version; the offsets are BODY-RELATIVE and stated below instead, because a
# version bump that does not change the EMITTED CODE keeps them.
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "bin\Debug\net11.0\ScriptLangCore.exe"
$dir = Join-Path $root "assets\language\repl"
$tmp = Join-Path $env:TEMP "bc_fix"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Find-Pattern([byte[]]$hay, [byte[]]$needle) {
    $hits = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -le ($hay.Length - $needle.Length); $i++) {
        $ok = $true
        for ($j = 0; $j -lt $needle.Length; $j++) { if ($hay[$i + $j] -ne $needle[$j]) { $ok = $false; break } }
        if ($ok) { $hits.Add($i) }
    }
    return $hits
}
function Md5Of([string]$p) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $h = $md5.ComputeHash([System.IO.File]::ReadAllBytes($p))
    return (($h | ForEach-Object { $_.ToString('x2') }) -join '')
}
# The compiled CODE array of ONE function, read out of `--dump-bytecode`'s own output: a header line
# `== <name> code=<N> ... ==` followed by a CODE: block of space-separated hex. The header states the length,
# so exactly N bytes are taken (the CONSTS section that follows is not hex).
function Get-CodeArray([string]$source, [string]$fnName) {
    $dump = & $exe --dump-bytecode $source 2>&1
    for ($i = 0; $i -lt $dump.Count; $i++) {
        if ($dump[$i] -notmatch '^==\s+(\S+)\s+code=(\d+)\s') { continue }
        if ($Matches[1] -ne $fnName) { continue }
        $need = [int]$Matches[2]
        $bytes = New-Object System.Collections.Generic.List[byte]
        for ($j = $i + 1; $j -lt $dump.Count -and $bytes.Count -lt $need; $j++) {
            $line = ("" + $dump[$j]).Trim()
            if ($line -eq '' -or $line -eq 'CODE:') { continue }
            if ($line -notmatch '^[0-9A-Fa-f ]+$') { break }
            foreach ($h in ($line -split '\s+')) {
                if ($h -match '^[0-9A-Fa-f]{2}$') { $bytes.Add([Convert]::ToByte($h, 16)) }
            }
        }
        if ($bytes.Count -ge $need) { return , $bytes[0..($need - 1)] }
        return $null
    }
    return $null
}

# body-relative corruption offsets, documented in the provenance block of vm_verify_refused.slc
$sources = @(
    @{ Name = "_verify_bad_body.bin";  Fn = "probe";    Src = "int probe() { return 7; }";                                                    Rel = @(5, 5);     Val = @(0xFF, 0xFF) }
    @{ Name = "_verify_bad_index.bin"; Fn = "idxProbe"; Src = "int idxProbe() { return 7; }";                                                  Rel = @(6, 6);     Val = @(0x05, 0x05) }
    @{ Name = "_verify_bad_jump.bin";  Fn = "brProbe";  Src = "int brProbe(int n) { if (n > 0) { return 1; } return 0; }";                      Rel = @(12, 13);   Val = @(0xFE, 0xFF) }
    @{ Name = "_verify_bad_range.bin"; Fn = "brProbe";  Src = "int brProbe(int n) { if (n > 0) { return 1; } return 0; }";                      Rel = @(12, 13);   Val = @(0xFF, 0x7F) }
    @{ Name = "_verify_bad_depth.bin"; Fn = "brProbe";  Src = "int brProbe(int n) { if (n > 0) { return 1; } return 0; }";                      Rel = @(12, 13);   Val = @(0xFC, 0xFF) }
    # The two COMPOUND-operand rules: a MAKE_LAMBDA capture's LOCAL index (the executor reads locals[slot]
    # with it) and a FOREACH_NEXT loop-var type CONST. Both bytes are single-byte fields of their instruction,
    # so patching the same byte twice is how the row shape states "one byte, not a pair": the capture pair's
    # local index sits at +3 of the MAKE_LAMBDA (opcode, lambda index, capture count, then the pair) and the
    # const's LOW byte at +3 of the FOREACH_NEXT (opcode, endLo, endHi, then the const). Both bodies are laid
    # out so those are body bytes 18 and 28, and the layout does not move with the source's line numbers.
    @{ Name = "_verify_bad_capture.bin"; Fn = "capProbe"; Src = "int capProbe() { var a = 1; var f = fun () { return a; }; return f(); }";                                                    Rel = @(18, 18); Val = @(0x09, 0x09) }
    @{ Name = "_verify_bad_foreach.bin"; Fn = "feProbe";  Src = "int feProbe(int[] xs) { var sum = 0; foreach (int x in xs) { sum = sum + x; } return sum; }";                                 Rel = @(28, 28); Val = @(0x09, 0x09) }
)

"=== the _verify_bad_*.bin fixtures, re-cut ==="
foreach ($case in $sources) {
    $sf = Join-Path $tmp ($case.Name + ".slc")
    [System.IO.File]::WriteAllText($sf, $case.Src)
    $code = Get-CodeArray $sf $case.Fn
    if ($code -eq $null) { "  $($case.Name) : could not read $($case.Fn)'s code array from --dump-bytecode"; continue }
    $raw = Join-Path $tmp ($case.Name + ".raw")
    & $exe --compile $sf $raw 2>&1 | Out-Null
    $b = [System.IO.File]::ReadAllBytes($raw)
    $hits = Find-Pattern $b ([byte[]]$code)
    if ($hits.Count -eq 0) { "  $($case.Name) : the code array is not in the blob"; continue }
    $start = $hits[$hits.Count - 1]
    $b[$start + $case.Rel[0]] = [byte]$case.Val[0]
    $b[$start + $case.Rel[1]] = [byte]$case.Val[1]
    $out = Join-Path $dir $case.Name
    # STAMP THE DIGEST AFTER THE PATCH, never before. A bundle carries a SHA-256 over every byte outside a 32-byte field
    # at offset 12, so a fixture patched AFTER stamping is refused by the DIGEST and never reaches the verifier rule it
    # exists to exercise (measured: vm_program_entry 9a reported "the digest differs from byte 0").
    $zeroed = [byte[]]$b.Clone()
    for ($z = 12; $z -lt 44; $z++) { $zeroed[$z] = 0 }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $digest = $sha.ComputeHash($zeroed)
    for ($z = 0; $z -lt 32; $z++) { $b[12 + $z] = $digest[$z] }
    [System.IO.File]::WriteAllBytes($out, $b)
    "  {0,-24} {1,4} B  body at {2} (code {3} B, of {4} hit(s)), patched {5} := 0x{6:X2} and {7} := 0x{8:X2}  md5 {9}" -f `
        $case.Name, $b.Length, $start, $code.Length, $hits.Count, ($start + $case.Rel[0]), $case.Val[0], ($start + $case.Rel[1]), $case.Val[1], (Md5Of $out)
}
