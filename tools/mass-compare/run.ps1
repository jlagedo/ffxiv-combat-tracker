<#
.SYNOPSIS
  Corpus-scale ACT-engine parity: prove our Fct.Compat.Act aggregation matches the real ACT binary,
  fed the identical plugin-produced swings, over a whole folder of Network_*.log files.

.DESCRIPTION
  The real FFXIV_ACT_Plugin is the sole parser. The pipeline:

    1. Build the net48 satellite, the net10 comparer, and (below) the ActOracle baseline tool.
    2. --mass-oracle: load the REAL FFXIV_ACT_Plugin once and capture its MasterSwing parse of every
       Network_*.log in -LogFolder, chronologically, as one continuous session -> <name>.oracle.tsv.
    3. ActOracle --folder: aggregate each <name>.oracle.tsv through the REAL ACT binary's
       EncounterData/CombatantData -> <name>.oracle.exports.tsv   (the baseline).
    4. --mass-engine-exports: aggregate the SAME <name>.oracle.tsv through OUR Fct.Compat.Act engine
       -> <name>.engine.exports.tsv   (under test).
    5. MassCompare: diff the two ExportVariables payloads, per file/combatant/key.

  This is the corpus version of Fct.Compat.Act.Tests/ExportVarsCompatTests. We never re-parse the
  log ourselves — parsing is the plugin's job; we only reproduce ACT's aggregation.

  Outputs go to -OutFolder (default tmp\mass-compare, gitignored). They contain real combatant
  names and are not committed.

.EXAMPLE
  ./tools/mass-compare/run.ps1
  ./tools/mass-compare/run.ps1 -MaxLines 200000        # quick: cap lines per file
  ./tools/mass-compare/run.ps1 -SkipOracle             # reuse existing oracle.tsv captures
#>
[CmdletBinding()]
param(
    [string]$LogFolder = "$env:APPDATA\Advanced Combat Tracker\FFXIVLogs",
    [string]$OutFolder = "$PSScriptRoot\..\..\tmp\mass-compare",
    [int]$MaxLines = 0,            # 0 = all lines
    [string]$ActDir = $env:ACT_DIR,
    [switch]$SkipOracle,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\..\.."
$OutFolder = [System.IO.Path]::GetFullPath($OutFolder)
New-Item -ItemType Directory -Force -Path $OutFolder | Out-Null

$satellite = "$repo\src\Fct.LegacyHost\bin\Debug\net48\Fct.LegacyHost.exe"

if (-not $SkipBuild) {
    Write-Host "==> building satellite + comparer" -ForegroundColor Cyan
    dotnet build "$repo\src\Fct.LegacyHost\Fct.LegacyHost.csproj" -c Debug -v q
    dotnet build "$repo\tools\mass-compare\MassCompare.csproj" -c Debug -v q
}

# The satellite is a WinExe (GUI subsystem). Start-Process -Wait does NOT reliably block on it in a
# non-interactive context, so launch via the .NET API and WaitForExit(), which always does.
function Invoke-Satellite([string]$arguments) {
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo.FileName = $satellite
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.Arguments = $arguments
    [void]$p.Start(); $p.WaitForExit()
    return $p.ExitCode
}

if (-not $SkipOracle) {
    Write-Host "==> capturing the plugin's parse of every log in $LogFolder" -ForegroundColor Cyan
    $max = if ($MaxLines -gt 0) { $MaxLines } else { 2147483647 }
    $ec = Invoke-Satellite("--mass-oracle `"$LogFolder`" `"$OutFolder`" $max")
    Write-Host "mass-oracle exit=$ec"
    Get-Content "$OutFolder\_mass-oracle.log" -Tail 6 -ErrorAction SilentlyContinue
}

# ---- Real-ACT baseline: plugin swings -> real ACT engine -> *.oracle.exports.tsv ----
if (-not $ActDir) { $ActDir = "E:\dev\Advanced Combat Tracker" }
$actExe = Join-Path $ActDir "Advanced Combat Tracker.exe"
if (-not (Test-Path $actExe)) {
    throw "ACT binary not found at $actExe (set -ActDir or `$env:ACT_DIR). The real-ACT baseline is required for the parity diff."
}

Write-Host "==> compiling ActOracle against the real ACT binary" -ForegroundColor Cyan
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$actOracleExe = "$repo\tools\act-oracle\ActOracle.exe"
& $csc -nologo -platform:x64 -reference:"$actExe" -reference:System.Drawing.dll `
    -reference:System.Windows.Forms.dll -out:"$actOracleExe" "$repo\tools\act-oracle\ActOracle.cs"
if ($LASTEXITCODE -ne 0) { throw "ActOracle compile failed" }

Write-Host "==> aggregating plugin swings through the REAL ACT engine (baseline)" -ForegroundColor Cyan
$env:ACT_DIR = $ActDir
$a = New-Object System.Diagnostics.Process
$a.StartInfo.FileName = $actOracleExe
$a.StartInfo.UseShellExecute = $false
$a.StartInfo.Arguments = "--folder `"$OutFolder`""
[void]$a.Start(); $a.WaitForExit()
Write-Host "act-oracle exit=$($a.ExitCode)"

# ---- Our engine: the SAME plugin swings -> Fct.Compat.Act -> *.engine.exports.tsv ----
Write-Host "==> aggregating the same plugin swings through OUR engine (under test)" -ForegroundColor Cyan
$ec = Invoke-Satellite("--mass-engine-exports `"$OutFolder`"")
Write-Host "mass-engine-exports exit=$ec"
Get-Content "$OutFolder\_mass-engine-exports.log" -Tail 6 -ErrorAction SilentlyContinue

# ---- Diff: ours vs real ACT, full ExportVariables payload ----
Write-Host "==> diffing the overlay payload (our engine vs real ACT)" -ForegroundColor Cyan
dotnet run --project "$repo\tools\mass-compare\MassCompare.csproj" -c Debug -- "$OutFolder"

Write-Host "`nExports summary: $OutFolder\exports-summary.txt"
Write-Host "Exports detail:  $OutFolder\exports-diff.txt"
