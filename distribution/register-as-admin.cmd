@echo off
setlocal
echo =======================================================
echo   Registering Singular Power Tools for Power BI Desktop
echo =======================================================

:: Check for Administrator elevation
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Administrator privileges required. Prompting for elevation...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "TARGET_DIR=%ProgramFiles(x86)%\Common Files\Microsoft Shared\Power BI Desktop\External Tools"
set "JSON_SRC=%~dp0SingularPowerTools.pbitool.json"

if not exist "%TARGET_DIR%" (
    mkdir "%TARGET_DIR%"
)

copy /y "%JSON_SRC%" "%TARGET_DIR%\SingularPowerTools.pbitool.json" >nul

echo.
echo [SUCCESS] Singular Power Tools has been registered!
echo Registered at: %TARGET_DIR%\SingularPowerTools.pbitool.json
echo.
echo Next steps:
echo 1. Launch or Restart Power BI Desktop.
echo 2. Open any report (or Demo PBI Report.pbip).
echo 3. Click the 'External Tools' tab on the top ribbon.
echo 4. You will see 'Singular Power Tools' ready to launch!
echo.
pause
