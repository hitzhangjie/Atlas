@echo off
rem Atlas current-branch build wrapper for double-click use.
rem Pass arguments through, e.g.: build-branch.cmd -Mode SingleFile -Zip
rem The real logic lives in build-branch.ps1.
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-branch.ps1" %*
set "ATLAS_EXIT=%ERRORLEVEL%"
if not "%ATLAS_EXIT%"=="0" (
    echo.
    echo Branch build failed. Exit code: %ATLAS_EXIT%
    pause
    exit /b %ATLAS_EXIT%
)
if "%~1"=="" pause
exit /b 0
