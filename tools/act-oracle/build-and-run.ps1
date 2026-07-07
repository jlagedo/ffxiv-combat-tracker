# Builds the ACT-layer differential oracle against the REAL Advanced Combat Tracker binary and
# regenerates the baselines that Fct.Compat.Act.Tests is held to: per slice it writes both the
# aggregate (<slice>.aggregate.tsv) and the ExportVariables string baseline
# (<slice>.exportvars.tsv, including the encounter-level *ENCOUNTER* rows OverlayPlugin reads).
#
# It also regenerates the plugin-in-the-loop baseline (combat-slice.plugin.exportvars.tsv) — the
# real FFXIV_ACT_Plugin.dll's own ACT_UIMods.UpdateACTTables registrations, enumerated directly
# (Job/ParryPct/Last10DPS/...) — if a plugin DLL can
# be found; otherwise that step is skipped with a warning so the ACT-core baselines above still
# regenerate without it.
#
#   ./build-and-run.ps1 [-ActDir <path>] [-Swings <tsv>] [-Out <tsv>] [-PluginDll <path>]
#
# With no -Swings/-Out it regenerates BOTH committed slices (combat-slice, combat-slice2), and the
# plugin baseline for combat-slice only ("one slice baseline" per the plan). Requires the real ACT
# install (default E:\dev\Advanced Combat Tracker). Dev-only: the baselines it produces are
# committed, so the normal test suite needs neither this tool nor an ACT install nor the plugin.
param(
    [string]$ActDir  = $env:ACT_DIR,
    [string]$Swings,
    [string]$Out,
    [string]$PluginDll = $env:FFXIV_PLUGIN_DLL
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

# The plugin-in-the-loop baseline: loads the real FFXIV_ACT_Plugin.dll and dumps every key its own
# ACT_UIMods.UpdateACTTables registers (enumerated, never hardcoded) to <slice>.plugin.exportvars.tsv.
function Invoke-PluginBaseline([string]$swingsTsv, [string]$exportsTsv) {
    if (-not $PluginDll) {
        $PluginDll = "E:\tmp\plugins\FFXIV_ACT_Plugin_3.0.2.3\FFXIV_ACT_Plugin.dll"
    }
    if (-not (Test-Path $PluginDll)) {
        Write-Warning "FFXIV_ACT_Plugin.dll not found ($PluginDll); skipping plugin-baseline regeneration. Set -PluginDll or `$env:FFXIV_PLUGIN_DLL."
        return
    }
    $env:FFXIV_PLUGIN_DLL = $PluginDll
    $swingsTsv  = (Resolve-Path $swingsTsv).Path
    $exportsTsv = [System.IO.Path]::GetFullPath($exportsTsv)
    & $exe --plugin-baseline $swingsTsv $exportsTsv
    if ($LASTEXITCODE -ne 0) { throw "plugin-baseline run failed for $swingsTsv" }
    Write-Host "Plugin baseline written: $exportsTsv"
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
    Invoke-PluginBaseline "$fixtures/combat-slice.oracle.tsv" "$fixtures/combat-slice.plugin.exportvars.tsv"
}
