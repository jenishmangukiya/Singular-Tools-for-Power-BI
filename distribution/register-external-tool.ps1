param (
    [switch]$ElevatedWorker
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$appExePath = "$env:LOCALAPPDATA\SingularPowerTools\SingularTools.App.exe"
$targetCommonDir = "${env:ProgramFiles(x86)}\Common Files\Microsoft Shared\Power BI Desktop\External Tools"

# Ensure executable is published
if (!(Test-Path $appExePath)) {
    Write-Host "Publishing Singular Power Tools to $env:LOCALAPPDATA\SingularPowerTools..." -ForegroundColor Cyan
    dotnet publish "$rootDir\src\SingularTools.App\SingularTools.App.csproj" -c Release -o "$env:LOCALAPPDATA\SingularPowerTools" --nologo
}

# Generate resolved JSON with absolute path and proper escaping
$escapedExe = $appExePath.Replace('\', '\\')
$jsonContent = @"
{
  "version": "1.0",
  "name": "Singular Power Tools",
  "description": "Command Palette and Page Organizer for Power BI reports (Ctrl+Alt+P)",
  "path": "$escapedExe",
  "arguments": "\"%server%\" \"%database%\"",
  "iconData": "image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAABHNCSVQICAgIfAhkiAAAAAlwSFlzAAAOxAAADsQBlSsOGwAAAG1JREFUWIW90rENwCAMRNEvY6RkEMZ/kTAiRQbA0eB8cbnL1rfvBvBmAeZ2r8p04P6x23n9XQG4G4B3G/BvAbh7gLsJuJuAu/2Vq7t3G3A3APcScDcBdwNwdz8D7ibg7gbg3QDcTcDdANwNwP1zF3v4QWwE0d0EAAAAAElFTkSuQmCC"
}
"@

$localJson = Join-Path $scriptDir "SingularPowerTools.pbitool.json"
Set-Content -Path $localJson -Value $jsonContent -Encoding utf8

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (!$isAdmin) {
    Write-Host "Administrator permissions are required to write to Power BI's Common Files folder." -ForegroundColor Yellow
    Write-Host "Requesting elevation via Windows UAC prompt..." -ForegroundColor Cyan

    $workerScript = @"
if (!(Test-Path '$targetCommonDir')) {
    New-Item -ItemType Directory -Path '$targetCommonDir' -Force | Out-Null
}
Copy-Item -Path '$localJson' -Destination (Join-Path '$targetCommonDir' 'SingularPowerTools.pbitool.json') -Force
"@
    $tempPs1 = Join-Path $env:TEMP "install_pbi_tool.ps1"
    Set-Content -Path $tempPs1 -Value $workerScript -Encoding utf8

    Start-Process -FilePath "powershell.exe" -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$tempPs1`"" -Verb RunAs -Wait
    Remove-Item $tempPs1 -ErrorAction SilentlyContinue
} else {
    if (!(Test-Path $targetCommonDir)) {
        New-Item -ItemType Directory -Path $targetCommonDir -Force | Out-Null
    }
    Copy-Item -Path $localJson -Destination (Join-Path $targetCommonDir "SingularPowerTools.pbitool.json") -Force
}

$registeredFile = Join-Path $targetCommonDir "SingularPowerTools.pbitool.json"
if (Test-Path $registeredFile) {
    Write-Host "`nSUCCESS! Singular Power Tools registered in Power BI Desktop!" -ForegroundColor Green
    Write-Host "File installed: $registeredFile" -ForegroundColor White
    Write-Host "`nSteps to see the tool in Power BI Desktop:" -ForegroundColor Cyan
    Write-Host "1. Close and Re-open Power BI Desktop."
    Write-Host "2. Look at the top ribbon -> 'External Tools' tab."
    Write-Host "3. You will see 'Singular Power Tools' with its icon!"
    Write-Host "4. Click the button, or simply press 'Ctrl + Alt + P' anytime Power BI Desktop is focused."
} else {
    Write-Host "Notice: The file was not written to Common Files. Please run the script as Administrator." -ForegroundColor Red
}
