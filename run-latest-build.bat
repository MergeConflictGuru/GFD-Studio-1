@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-latest-build.ps1"
if errorlevel 1 (
    echo Failed to download the latest GFD Studio build.
    exit /b 1
)

start "GFD Studio" "%~dp0GFDStudio-binary\GFDStudio.exe"
endlocal
