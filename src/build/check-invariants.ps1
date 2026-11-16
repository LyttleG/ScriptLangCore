# src/build/check-invariants.ps1, the structural invariants for a box without bash.
#
# check-invariants.sh is the authority and stays the authority. This runs the checks that have been PORTED so far, in
# the shell loop's order, and says which ones are still missing rather than implying the set is complete. Each ported
# check delegates to its .sh twin when bash exists, unless -Native is given.
#
# Ported so far: check-dup-friendly-maps, check-opcode-coverage. Still to port (the shell loop's order):
# check-suite-accounting, check-native-load-sites, check-platform-branches, check-suite-protocol, check-doc-structure,
# check-doc-voice; and the two that stand outside the loop: check-native-matrix, check-platform-parity.
param([switch]$Native, [switch]$Quiet)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $root
$ported = @("check-dup-friendly-maps.ps1", "check-opcode-coverage.ps1")
$fail = 0
foreach ($c in $ported) {
    Write-Host "-- $c"
    $args = @()
    if ($Native) { $args += "-Native" }
    if ($Quiet) { $args += "-Quiet" }
    & (Join-Path $PSScriptRoot $c) @args
    if ($LASTEXITCODE -ne 0) { $fail = 1 }
}
if ($fail -eq 0) { Write-Host "all ported invariants hold" } else { Write-Host "invariant violations above, see AGENTS.md for why they exist" }
Write-Host "note: this port covers $($ported.Count) of the 10 checks; run src/build/check-invariants.sh where bash exists"
exit $fail