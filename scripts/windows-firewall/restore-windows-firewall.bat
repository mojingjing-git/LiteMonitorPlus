@echo off
REM Restore the one-time firewall backup.
REM Run this file from an elevated terminal; the script does not request elevation.
setlocal
set "LOG=%~dp0fw-restore.log"
if exist "%LOG%" del /f /q "%LOG%"
where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo PowerShell 7 was not found in PATH.>&2
  exit /b 9009
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0restore-windows-firewall.ps1" %* > "%LOG%" 2>&1
set "CODE=%ERRORLEVEL%"
echo EXITCODE=%CODE%>>"%LOG%"
type "%LOG%"
exit /b %CODE%
