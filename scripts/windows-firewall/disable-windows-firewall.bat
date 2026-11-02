@echo off
REM Capture a one-time backup and disable the Windows firewall profiles.
REM Run this file from an elevated terminal; the script does not request elevation.
setlocal
set "LOG=%~dp0fw-disable.log"
if exist "%LOG%" del /f /q "%LOG%"
where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo PowerShell 7 was not found in PATH.>&2
  exit /b 9009
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0disable-windows-firewall.ps1" %* > "%LOG%" 2>&1
set "CODE=%ERRORLEVEL%"
echo EXITCODE=%CODE%>>"%LOG%"
type "%LOG%"
exit /b %CODE%
