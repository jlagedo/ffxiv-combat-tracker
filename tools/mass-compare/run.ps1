<#
.SYNOPSIS
  Massive parse-compat run: diff our native parser against ACT's parse over a whole log folder.

.DESCRIPTION
  1. Builds the net48 satellite and the net10 comparer.
  2. Runs the satellite's --mass-oracle: loads the REAL FFXIV_ACT_Plugin once and captures ACT's
     authoritative parse of every Network_*.log in -LogFolder (chronological, continuous state).
  3. Runs MassCompare: parses the same logs with our clean-room parser and bit-diffs each file
     against the oracle, writing per-file TSVs, report.tsv, details.txt and summary.txt.

  Outputs go to -OutFolder (default tmp\mass-compare, gitignored). They contain real combatant
  names and are not committed.

.EXAMPLE
  ./tools/mass-compare/run.ps1
  ./tools/mass-compare/run.ps1 -MaxLines 200000        # quick: cap lines per file
  ./tools/mass-compare/run.ps1 -SkipOracle             # re-diff using existing oracle TSVs
#>
[CmdletBinding()]
param(
    [string]$LogFolder = "$env:APPDATA\Advanced Combat Tracker\FFXIVLogs",
    [string]$OutFolder = "$PSScriptRoot\..\..\tmp\mass-compare",
    [int]$MaxLines = 0,            # 0 = all lines
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

if (-not $SkipOracle) {
    Write-Host "==> capturing ACT oracle for every log in $LogFolder" -ForegroundColor Cyan
    $max = if ($MaxLines -gt 0) { $MaxLines } else { 2147483647 }
    # The satellite is a WinExe (GUI subsystem). Start-Process -Wait does NOT reliably block on it
    # in a non-interactive context, so launch via the .NET API and WaitForExit(), which always does.
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo.FileName = $satellite
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.Arguments = "--mass-oracle `"$LogFolder`" `"$OutFolder`" $max"
    [void]$p.Start(); $p.WaitForExit()
    Write-Host "oracle exit=$($p.ExitCode)"
    Get-Content "$OutFolder\_mass-oracle.log" -Tail 6 -ErrorAction SilentlyContinue

    Write-Host "==> dumping game-data tables (action categories, status names, DoT/shield defs)" -ForegroundColor Cyan
    $d = New-Object System.Diagnostics.Process
    $d.StartInfo.FileName = $satellite
    $d.StartInfo.UseShellExecute = $false
    $d.StartInfo.Arguments = "--dump-tables `"$OutFolder`""
    [void]$d.Start(); $d.WaitForExit()
    Write-Host "dump-tables exit=$($d.ExitCode)"
    Get-Content "$OutFolder\_dump-tables.log" -Tail 4 -ErrorAction SilentlyContinue
}

Write-Host "==> diffing our parse vs the oracle" -ForegroundColor Cyan
dotnet run --project "$repo\tools\mass-compare\MassCompare.csproj" -c Debug -- `
    "$LogFolder" "$OutFolder" "$OutFolder"

Write-Host "`nReport:  $OutFolder\report.tsv"
Write-Host "Details: $OutFolder\details.txt"
Write-Host "Summary: $OutFolder\summary.txt"
