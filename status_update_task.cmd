@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0OsEngineUpdateTask.ps1" -Action Status %*
echo.
pause
exit /b %ERRORLEVEL%
