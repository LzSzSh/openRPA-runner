@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-win-x64.ps1" -Mode Standalone
pause
