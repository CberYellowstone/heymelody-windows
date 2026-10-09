@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-3D-Demo.ps1" %*
if errorlevel 1 pause
