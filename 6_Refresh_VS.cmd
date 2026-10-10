@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0RefreshVs.ps1" %*
echo.
pause
exit /b %ERRORLEVEL%
