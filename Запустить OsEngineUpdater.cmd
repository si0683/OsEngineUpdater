@echo off
rem Double-click: downloads OsEngineUpdater.exe from GitHub Releases (first time) and starts it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start.ps1"
if errorlevel 1 pause
