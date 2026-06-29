# Builds the ACT-layer differential oracle against the REAL Advanced Combat Tracker binary and
# regenerates the baselines that Fct.Compat.Act.Tests is held to: per slice it writes both the
# aggregate (<slice>.aggregate.tsv) and the ExportVariables string baseline
# (<slice>.exportvars.tsv, including the encounter-level *ENCOUNTER* rows OverlayPlugin reads).
#
#   ./build-and-run.ps1 [-ActDir <path>] [-Swings <tsv>] [-Out <tsv>]
#
# With no -Swings/-Out it regenerates BOTH committed slices (combat-slice, combat-slice2).
# Requires the real ACT install (default E:\dev\Advanced Combat Tracker). Dev-only: the baselines
# it produces are committed, so the normal test suite needs neither this tool nor an ACT install.
param(
    [string]$ActDir  = $env:ACT_DIR,
    [string]$Swings,
    [string]$Out
)

if (-not $ActDir) { $ActDir = "E:\dev\Advanced Combat Tracker" }
$act = Join-Path $ActDir "Advanced Combat Tracker.exe"
if (-not (Test-Path $act)) { throw "ACT binary not found: $act (set -ActDir or `$env:ACT_DIR)" }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = Join-Path $PSScriptRoot "ActOracle.exe"
$src = Join-Path $PSScriptRoot "ActOracle.cs"

& $csc -nologo -platform:x64 -reference:"$act" -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -out:"$exe" "$src"
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
$env:ACT_DIR = $ActDir

$fixtures = [System.IO.Path]::GetFullPath("$PSScriptRoot/../../tests/Fct.Compat.Act.Tests/fixtures")

# One slice → (oracle.tsv) → aggregate.tsv + exportvars.tsv via the 3-arg ActOracle invocation.
function Invoke-Slice([string]$swingsTsv, [string]$aggregateTsv) {
    $swingsTsv = (Resolve-Path $swingsTsv).Path
    $aggregateTsv = [System.IO.Path]::GetFullPath($aggregateTsv)
    $exportsTsv = $aggregateTsv -replace '\.aggregate\.tsv$', '.exportvars.tsv'
    & $exe $swingsTsv $aggregateTsv $exportsTsv
    if ($LASTEXITCODE -ne 0) { throw "oracle run failed for $swingsTsv" }
    Write-Host "Baselines written: $aggregateTsv ; $exportsTsv"
}

if ($Swings -or $Out) {
    if (-not $Swings) { $Swings = "$fixtures/combat-slice.oracle.tsv" }
    if (-not $Out)    { $Out    = "$fixtures/combat-slice.aggregate.tsv" }
    Invoke-Slice $Swings $Out
}
else {
    foreach ($slice in @("combat-slice", "combat-slice2")) {
        Invoke-Slice "$fixtures/$slice.oracle.tsv" "$fixtures/$slice.aggregate.tsv"
    }
}
