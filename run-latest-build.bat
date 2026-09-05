@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-latest-build.ps1" -Launch
if errorlevel 1 (
    echo Failed to download the latest GFD Studio build.
    exit /b 1
)

endlocal
