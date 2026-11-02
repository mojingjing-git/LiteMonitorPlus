@echo off
REM Inspect and clean only LiteMonitor-owned test firewall rules.
REM Run this file from an elevated terminal; the script does not request elevation.
setlocal
set "LOG=%~dp0fw-autoclean.log"
if exist "%LOG%" del /f /q "%LOG%"
where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo PowerShell 7 was not found in PATH.>&2
  exit /b 9009
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0cleanup-auto-firewall-rules.ps1" %* > "%LOG%" 2>&1
set "CODE=%ERRORLEVEL%"
echo EXITCODE=%CODE%>>"%LOG%"
type "%LOG%"
exit /b %CODE%
