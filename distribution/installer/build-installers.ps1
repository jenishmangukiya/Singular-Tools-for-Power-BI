# build-installers.ps1
#
# Publishes Singular Tools for each architecture and compiles a matching
# per-user Setup.exe with Inno Setup (ISCC.exe).
#
# Usage:
#   pwsh -ExecutionPolicy Bypass -File distribution/installer/build-installers.ps1
#   pwsh ... -Version 1.2.0            # stamp a specific version
#   pwsh ... -Architectures x64,arm64  # build a subset
#
# Output: distribution/installer/output/SingularTools-<version>-<arch>-Setup.exe

[CmdletBinding()]
param(
    [string[]]$Architectures = @('x64', 'x86', 'arm64'),
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent (Split-Path -Parent $scriptDir)
$csproj = Join-Path $rootDir 'src\SingularTools.App\SingularTools.App.csproj'
$iss = Join-Path $scriptDir 'SingularTools.iss'
$outputDir = Join-Path $scriptDir 'output'
$publishRoot = Join-Path $scriptDir 'publish'

# Inno Setup's compiler: honour a copy on PATH, else the default install dirs.
function Resolve-Iscc {
    $onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    throw "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup"
}

# Maps our arch names to the two values the .iss needs.
function Get-ArchDefines([string]$arch) {
    switch ($arch) {
        'x64'   { return @{ Allowed = 'x64compatible' } }
        'x86'   { return @{ Allowed = 'x86compatible' } }
        'arm64' { return @{ Allowed = 'arm64' } }
        default { throw "Unsupported architecture: $arch" }
    }
}

$iscc = Resolve-Iscc
Write-Host "Using Inno Setup compiler: $iscc" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

foreach ($arch in $Architectures) {
    $defines = Get-ArchDefines $arch
    $publishDir = Join-Path $publishRoot $arch

    Write-Host "`n=== Publishing $arch ===" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish $csproj `
        -c $Configuration `
        -r "win-$arch" `
        --self-contained true `
        -p:Platform=$arch `
        -o $publishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }

    $exe = Join-Path $publishDir 'SingularTools.App.exe'
    if (!(Test-Path $exe)) { throw "Expected published exe not found: $exe" }

    # Base64 PNG for the Power BI External Tools ribbon icon. Encoding here (in
    # PowerShell) keeps binary data out of the Inno Pascal script; the .iss just
    # splices the string in.
    $iconPath = Join-Path $publishDir 'Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png'
    if (!(Test-Path $iconPath)) { throw "Icon not found in publish output: $iconPath" }
    $iconData = 'image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($iconPath))

    Write-Host "=== Compiling installer ($arch) ===" -ForegroundColor Cyan
    & $iscc `
        "/DArchName=$arch" `
        "/DArchitecturesAllowed=$($defines.Allowed)" `
        "/DAppVersion=$Version" `
        "/DAppSourceDir=$publishDir" `
        "/DOutputDir=$outputDir" `
        "/DIconData=$iconData" `
        $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $arch" }
}

Write-Host "`nDone. Installers:" -ForegroundColor Green
Get-ChildItem (Join-Path $outputDir '*-Setup.exe') |
    Sort-Object Name |
    ForEach-Object { Write-Host "  $($_.FullName)" }
