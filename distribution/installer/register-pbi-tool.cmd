@echo off
setlocal EnableDelayedExpansion

:: register-pbi-tool.cmd
:: Registers (or unregisters) Singular Tools in Power BI Desktop's External Tools
:: ribbon. Writes SingularTools.pbitool.json next to this script's app folder and,
:: when permission allows, into Power BI's Common Files External Tools folder.
::
:: Usage:
::   register-pbi-tool.cmd            (interactive, elevates if needed)
::   register-pbi-tool.cmd -Quiet
::   register-pbi-tool.cmd -Unregister
::   register-pbi-tool.cmd -Unregister -Quiet

set "APP_DIR=%~dp0"
set "APP_DIR=%APP_DIR:~0,-1%"
set "APP_EXE=%APP_DIR%\SingularTools.App.exe"
set "LOCAL_JSON=%APP_DIR%\SingularTools.pbitool.json"
set "TARGET_DIR=%ProgramFiles(x86)%\Common Files\Microsoft Shared\Power BI Desktop\External Tools"
set "TARGET_JSON=%TARGET_DIR%\SingularTools.pbitool.json"

set "QUIET="
set "UNREGISTER="
for %%A in (%*) do (
  if /i "%%~A"=="-Quiet" set "QUIET=1"
  if /i "%%~A"=="-Unregister" set "UNREGISTER=1"
)

:: ---- Elevation handshake -------------------------------------------------
:: Writing to Common Files may need admin; re-launch elevated once, passing the
:: same switches through.
net session >nul 2>&1
if %errorLevel% neq 0 (
  if not defined QUIET echo Administrator privileges required. Requesting elevation...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs -Wait"
  exit /b
)

if defined UNREGISTER goto :unregister

:: ---- Build the manifest with a small PowerShell script --------------------
:: A temp .ps1 keeps the JSON formatting and escaping out of cmd quotation hell.
set "PS1=%TEMP%\singular-tools-register-%RANDOM%.ps1"
> "%PS1%" echo $ErrorActionPreference = 'Stop'
>>"%PS1%" echo $exe = '%APP_EXE%'
>>"%PS1%" echo $iconPath = Join-Path '%APP_DIR%' 'Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png'
>>"%PS1%" echo $icon = ''
>>"%PS1%" echo if (Test-Path $iconPath) { $icon = 'image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($iconPath)) }
>>"%PS1%" echo $json = [ordered]@{
>>"%PS1%" echo   version = '1.0'
>>"%PS1%" echo   name = 'Singular Tools'
>>"%PS1%" echo   description = 'Utilities for Power BI Desktop authors'
>>"%PS1%" echo   path = $exe
>>"%PS1%" echo   arguments = '"%%server%%" "%%database%%"'
>>"%PS1%" echo   iconData = $icon
>>"%PS1%" echo } ^| ConvertTo-Json
>>"%PS1%" echo Set-Content -Path '%LOCAL_JSON%' -Value $json -Encoding utf8

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
del /q "%PS1%" >nul 2>&1
if not exist "%LOCAL_JSON%" (
  if not defined QUIET echo Failed to write %LOCAL_JSON%
  exit /b 1
)

:: ---- Register with Power BI Desktop -------------------------------------
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%" >nul 2>&1
copy /y "%LOCAL_JSON%" "%TARGET_JSON%" >nul 2>&1
if errorlevel 1 (
  if not defined QUIET (
    echo.
    echo Notice: could not copy the manifest into Power BI's External Tools folder.
    echo Ensure Power BI Desktop is installed and run this script as Administrator.
  )
  exit /b 1
)

if not defined QUIET (
  echo.
  echo [SUCCESS] Singular Tools registered with Power BI Desktop.
  echo Manifest: %TARGET_JSON%
  echo Restart Power BI Desktop, then look under the 'External Tools' ribbon tab.
)
exit /b 0

:unregister
if exist "%LOCAL_JSON%" del /q "%LOCAL_JSON%" >nul 2>&1
if exist "%TARGET_JSON%" del /q "%TARGET_JSON%" >nul 2>&1
if not defined QUIET echo Singular Tools unregistered from Power BI Desktop.
exit /b 0
