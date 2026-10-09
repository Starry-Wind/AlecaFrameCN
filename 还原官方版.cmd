@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-AlecaFrame-ZH.ps1"
echo.
pause
