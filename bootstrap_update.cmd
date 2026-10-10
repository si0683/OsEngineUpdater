@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UpdateOsEngine.ps1" -Bootstrap %*
echo.
pause
exit /b %ERRORLEVEL%
