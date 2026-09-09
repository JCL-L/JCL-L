@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\HardwarePreflight.ps1"
echo.
pause
