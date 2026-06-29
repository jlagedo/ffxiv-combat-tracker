#!/usr/bin/env pwsh
# Build + run the full test suite for FFXIV Combat Tracker.
#
#   ./test.ps1                 # Debug, all projects (integration tests run against staged satellite)
#   ./test.ps1 -Configuration Release
#   ./test.ps1 -Unit           # skip the satellite integration tests (fast, no process launch)
#   ./test.ps1 -Filter "Dnum"  # pass a --filter expression through to dotnet test
#
# Integration tests need the net48 satellite staged, so Fct.App is built first. Tests that
# need the real FFXIV plugin / a Network log skip automatically when those are absent.

[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [switch]$Unit,
    [string]$Filter
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Push-Location $root
try {
    Write-Host "==> Staging satellite (build Fct.App, $Configuration)" -ForegroundColor Cyan
    dotnet build "src/Fct.App/Fct.App.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "satellite build failed" }

    $projects = @(
        "tests/Fct.Compat.Act.Tests/Fct.Compat.Act.Tests.csproj",
        "tests/Fct.App.Tests/Fct.App.Tests.csproj",
        "tests/Fct.Parser.Native.Tests/Fct.Parser.Native.Tests.csproj",
        "tests/Fct.Parser.Legacy.Tests/Fct.Parser.Legacy.Tests.csproj"
    )
    if (-not $Unit) {
        $projects += "tests/Fct.Integration.Tests/Fct.Integration.Tests.csproj"
    }

    $failed = @()
    foreach ($p in $projects) {
        Write-Host "==> dotnet test $p" -ForegroundColor Cyan
        $args = @("test", $p, "-c", $Configuration, "--nologo")
        if ($Filter) { $args += @("--filter", $Filter) }
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { $failed += $p }
    }

    Write-Host ""
    if ($failed.Count -gt 0) {
        Write-Host "FAILED:" -ForegroundColor Red
        $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "All test projects passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
