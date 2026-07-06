#!/usr/bin/env pwsh
# Build + run the full test suite for FFXIV Combat Tracker.
#
#   ./test.ps1                 # Debug, all projects (integration tests run against staged satellite)
#   ./test.ps1 -Configuration Release
#   ./test.ps1 -Unit           # skip the satellite integration tests (fast, no process launch)
#   ./test.ps1 -Filter "Dnum"  # pass a --filter expression through to dotnet test
#   ./test.ps1 -Dist           # also publish dist\<mode> and run the P9c dist-tree e2e gate
#
# Integration tests need the net48 satellite staged, so Fct.App is built first. Tests that
# need the real FFXIV plugin / a Network log skip automatically when those are absent.
#
# -Dist opt-in: publishes the distributable tree (`dotnet run --project build -- <mode>`) and arms
# DistTreeGateTests via FCT_DIST_MODE so the shipped dist\<mode> tree is exercised end-to-end. The
# gate self-skips when FCT_DIST_MODE is unset, so a normal run never touches dist\.

[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [switch]$Unit,
    [string]$Filter,
    [switch]$Dist,
    [string]$DistMode
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Push-Location $root
try {
    Write-Host "==> Staging satellite (build Fct.App, $Configuration)" -ForegroundColor Cyan
    dotnet build "src/Fct.App/Fct.App.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "satellite build failed" }

    if ($Dist) {
        if (-not $DistMode) { $DistMode = $Configuration.ToLowerInvariant() }
        Write-Host "==> Publishing dist tree (dotnet run --project build -- $DistMode)" -ForegroundColor Cyan
        dotnet run --project build -- $DistMode
        if ($LASTEXITCODE -ne 0) { throw "dist publish failed" }
        $env:FCT_DIST_MODE = $DistMode   # arms DistTreeGateTests (Fct.Integration.Tests)
        Write-Host "==> Dist gate armed: FCT_DIST_MODE=$DistMode" -ForegroundColor Cyan
    }

    $projects = @(
        "tests/Fct.Compat.Act.Tests/Fct.Compat.Act.Tests.csproj",
        "tests/Fct.App.Tests/Fct.App.Tests.csproj",
        "tests/Fct.Parser.Legacy.Tests/Fct.Parser.Legacy.Tests.csproj",
        "tests/Fct.Engine.Tests/Fct.Engine.Tests.csproj",
        "tests/Fct.FlowTests/Fct.FlowTests.csproj",
        "tests/Fct.Compat.Shim.Tests/Fct.Compat.Shim.Tests.csproj",
        "tests/Fct.Compat.Shim.E2E.Tests/Fct.Compat.Shim.E2E.Tests.csproj"
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
