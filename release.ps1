#!/usr/bin/env pwsh
# Build a distributable release of FFXIV Combat Tracker into a directory.
#
#   ./release.ps1                          # self-contained single-file win-x64 -> .\dist
#   ./release.ps1 -OutputDir C:\out\fct    # release into a specific directory
#   ./release.ps1 -FrameworkDependent      # needs .NET 10 Desktop Runtime on the target
#   ./release.ps1 -SingleFile:$false       # expand the host runtime into loose DLLs
#   ./release.ps1 -Version 0.2.0           # stamp assembly/file version
#   ./release.ps1 -Zip                     # also produce <OutputDir>.zip
#
# The app is two processes in one runtime tree:
#   <OutputDir>\Fct.App.exe              the .NET 10 Avalonia host
#   <OutputDir>\satellite\Fct.LegacyHost.exe   the .NET Framework 4.8 satellite
# The host launches the satellite from satellite\ at runtime, so both halves are
# published and assembled here. The real legacy plugins (FFXIV_ACT_Plugin,
# OverlayPlugin, ...) are NOT bundled — they load from the user's ACT install.
#
# By default the .NET 10 host is published as a single compressed self-extracting
# Fct.App.exe (no loose runtime DLLs). The net48 satellite has no single-file
# option, so satellite\ stays a small set of DLLs regardless.
#
# Builds are Debug by default and ship full portable .pdb debug symbols alongside
# the binaries so the distributed app is debuggable and produces symbolicated
# stack traces. Pass -Configuration Release for an optimized build.

[CmdletBinding()]
param(
    [string]$OutputDir = "dist",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Debug",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [bool]$SingleFile = $true,
    [string]$Version,
    [switch]$Zip,
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$selfContained = -not $FrameworkDependent

$hostProj = Join-Path $root "src\Fct.App\Fct.App.csproj"
$satProj  = Join-Path $root "src\Fct.LegacyHost\Fct.LegacyHost.csproj"

if (-not [System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir = Join-Path $root $OutputDir }
$satOut = Join-Path $OutputDir "satellite"

# Version is optional; pass through to both publishes when given.
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

# Always emit full portable debug symbols and keep the .pdb files in the output, so a
# distributed build can be debugged and produces symbolicated stack traces.
$symbolArgs = @(
    "-p:DebugType=portable",
    "-p:DebugSymbols=true"
)

# Single-file applies to the .NET 10 host only — net48 has no single-file publish. Bundle the
# native libs into the self-extractor and compress so the output is one Fct.App.exe, not a tree
# of runtime DLLs.
$hostSingleFileArgs = @()
if ($SingleFile) {
    $hostSingleFileArgs = @(
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true"
    )
}

# ReadyToRun precompiles the host's IL to native images so launch skips the JIT — it roughly
# halves warm startup. It only applies at publish time with a RID, so it never slows `dotnet
# build`. Loose builds (-SingleFile:$false) start fastest because they also skip the single-file
# self-extract step; single-file trades a slower first run (extract + AV scan) for one tidy exe.
$hostR2RArgs = @("-p:PublishReadyToRun=true")

$mode = if ($selfContained) { "self-contained" } else { "framework-dependent" }
Write-Host "==> FFXIV Combat Tracker release" -ForegroundColor Cyan
Write-Host "    configuration : $Configuration"
Write-Host "    runtime       : $Runtime ($mode)"
Write-Host "    output        : $OutputDir"
if ($Version) { Write-Host "    version       : $Version" }
Write-Host ""

Push-Location $root
try {
    # 1. Clean the output directory.
    if (Test-Path $OutputDir) {
        if ($NoClean) {
            Write-Host "==> Reusing existing $OutputDir (-NoClean)" -ForegroundColor Yellow
        }
        else {
            Write-Host "==> Cleaning $OutputDir" -ForegroundColor Cyan
            Remove-Item $OutputDir -Recurse -Force
        }
    }

    # 2. Publish the .NET 10 host into the root of the output directory.
    Write-Host "==> Publishing host (Fct.App, net10.0)" -ForegroundColor Cyan
    dotnet publish $hostProj `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContained `
        -o $OutputDir `
        --nologo `
        @versionArgs `
        @symbolArgs `
        @hostSingleFileArgs `
        @hostR2RArgs
    if ($LASTEXITCODE -ne 0) { throw "host publish failed" }

    # The host build stages the satellite into its own bin (not here); drop any copy that
    # landed in the publish tree so the satellite folder is exactly one clean publish.
    if (Test-Path $satOut) { Remove-Item $satOut -Recurse -Force }

    # 3. Publish the .NET Framework 4.8 satellite into satellite\.
    Write-Host "==> Publishing satellite (Fct.LegacyHost, net48 x64)" -ForegroundColor Cyan
    dotnet publish $satProj `
        -c $Configuration `
        -o $satOut `
        --nologo `
        @versionArgs `
        @symbolArgs
    if ($LASTEXITCODE -ne 0) { throw "satellite publish failed" }

    # 4. Verify the two entry points exist.
    $hostExe = Join-Path $OutputDir "Fct.App.exe"
    $satExe  = Join-Path $satOut  "Fct.LegacyHost.exe"
    foreach ($exe in @($hostExe, $satExe)) {
        if (-not (Test-Path $exe)) { throw "expected output missing: $exe" }
    }

    # 5. Optional zip alongside the directory.
    if ($Zip) {
        $zipPath = "$OutputDir.zip"
        Write-Host "==> Zipping to $zipPath" -ForegroundColor Cyan
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath
    }

    $size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ""
    Write-Host "Release complete." -ForegroundColor Green
    Write-Host ("  {0,-22} {1}" -f "host:",      $hostExe)
    Write-Host ("  {0,-22} {1}" -f "satellite:", $satExe)
    Write-Host ("  {0,-22} {1:N1} MB" -f "size on disk:", ($size / 1MB))
    if ($Zip) { Write-Host ("  {0,-22} {1}" -f "archive:", "$OutputDir.zip") }
}
finally {
    Pop-Location
}
