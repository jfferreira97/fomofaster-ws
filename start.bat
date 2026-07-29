@echo off
setlocal

set "REPO=%~dp0"
set "BACKEND_DIR=%REPO%telegram-bot\TelegramBot"
set "SIDECAR_DIR=%REPO%ws-sidecar"

echo [start] Killing existing instances...
taskkill /FI "WINDOWTITLE eq FomoFaster-Backend*" /T /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq FomoFaster-Sidecar*" /T /F >nul 2>&1

REM Fallback: match by command line so we only ever kill THIS project's
REM dotnet/node processes, never unrelated ones running elsewhere on the box.
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*TelegramBot*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'node.exe' -and $_.CommandLine -like '*ws-sidecar*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1

echo [start] Starting backend...
start "FomoFaster-Backend" cmd /k "cd /d "%BACKEND_DIR%" && dotnet run"

echo [start] Waiting for backend to come up...
timeout /t 8 /nobreak >nul

echo [start] Starting sidecar...
start "FomoFaster-Sidecar" cmd /k "cd /d "%SIDECAR_DIR%" && npm start"

echo [start] Both processes launched in separate windows.
endlocal
