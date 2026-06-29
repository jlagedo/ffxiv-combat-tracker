# Builds the ACT-layer differential oracle against the REAL Advanced Combat Tracker binary and
# regenerates the aggregate baseline that Fct.Compat.Act.Tests is held to.
#
#   ./build-and-run.ps1 [-ActDir <path>] [-Swings <tsv>] [-Out <tsv>]
#
# Requires the real ACT install (default E:\dev\Advanced Combat Tracker). Dev-only: the baseline
# it produces is committed, so the normal test suite needs neither this tool nor an ACT install.
param(
    [string]$ActDir  = $env:ACT_DIR,
    [string]$Swings  = "$PSScriptRoot/../../tests/Fct.Parser.Native.Tests/fixtures/combat-slice.oracle.tsv",
    [string]$Out     = "$PSScriptRoot/../../tests/Fct.Compat.Act.Tests/fixtures/combat-slice.aggregate.tsv"
)

if (-not $ActDir) { $ActDir = "E:\dev\Advanced Combat Tracker" }
$act = Join-Path $ActDir "Advanced Combat Tracker.exe"
if (-not (Test-Path $act)) { throw "ACT binary not found: $act (set -ActDir or `$env:ACT_DIR)" }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = Join-Path $PSScriptRoot "ActOracle.exe"
$src = Join-Path $PSScriptRoot "ActOracle.cs"
# csc treats forward slashes in paths oddly; pass fully-resolved backslash paths.
$Swings = (Resolve-Path $Swings).Path
$Out = [System.IO.Path]::GetFullPath($Out)

& $csc -nologo -platform:x64 -reference:"$act" -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -out:"$exe" "$src"
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

$env:ACT_DIR = $ActDir
& $exe $Swings $Out
if ($LASTEXITCODE -ne 0) { throw "oracle run failed" }
Write-Host "Baseline written: $Out"
