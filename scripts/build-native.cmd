@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-native.ps1"
exit /b %ERRORLEVEL%
