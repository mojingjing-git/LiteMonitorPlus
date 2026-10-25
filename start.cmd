@echo off
setlocal
rem Double-click to launch Release. Use start.cmd -Build to rebuild first.
rem Keep delayed expansion disabled so paths containing ! are preserved.
setlocal DisableDelayedExpansion
set "LITEMONITOR_PWSH=pwsh.exe"
where pwsh.exe >nul 2>nul
if not errorlevel 1 goto launch
set "LITEMONITOR_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if exist "%LITEMONITOR_PWSH%" goto launch
echo PowerShell 7 was not found. Install it or add pwsh.exe to PATH.
set "LITEMONITOR_EXIT=2"
goto failed

:launch
"%LITEMONITOR_PWSH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start.ps1" %*
set "LITEMONITOR_EXIT=%ERRORLEVEL%"
if "%LITEMONITOR_EXIT%"=="0" exit /b 0

:failed
echo.
echo LiteMonitor++ could not start. See the error above.
echo Press any key to close this window.
pause >nul
exit /b %LITEMONITOR_EXIT%
