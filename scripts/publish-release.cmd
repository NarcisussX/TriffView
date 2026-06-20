@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-release.ps1" %*
exit /b %ERRORLEVEL%
