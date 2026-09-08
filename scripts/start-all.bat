@echo off
REM Lives in scripts\ alongside the other start-*.bat files it launches.
REM Kills any existing instances of this project's processes, then launches backend +
REM both fomo accounts + pump sidecar, each in its own window. The one script that
REM does a full clean restart — run this instead of any individual start-*.bat when
REM you want everything back to a known-good state.
REM NOTE: on pump-ws-sidecar's very first run ever, its window pops up a visible Chrome
REM pointed at pump.fun and waits up to 5 min for you to log in by hand. After that
REM one-time login the session persists and every future start-all.bat run is silent.

echo [start-all] Killing existing instances...
REM "dotnet run" hosts the compiled TelegramBot.exe as a child process — its own
REM CommandLine is just "dotnet run" with no path in it, so it can only be found
REM by killing TelegramBot.exe first and then its parent dotnet.exe alongside it.
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='TelegramBot.exe'\" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; if ($_.ParentProcessId) { Stop-Process -Id $_.ParentProcessId -Force -ErrorAction SilentlyContinue } }" >nul 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'node.exe' -and ($_.CommandLine -like '*fomo-ws-sidecar*' -or $_.CommandLine -like '*pump-ws-sidecar*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1

echo [start-all] Starting backend...
start "TelegramBot" cmd /k "%~dp0start-telegram-bot.bat"

echo [start-all] Waiting 10s for backend to bind :8000...
timeout /t 10 /nobreak >nul

echo [start-all] Starting fomo account 1...
start "fomo-ws-sidecar" cmd /k "%~dp0start-fomo-ws-sidecar.bat"

echo [start-all] Starting fomo account 2...
start "fomo-ws-sidecar-2" cmd /k "%~dp0start-fomo-ws-sidecar-2.bat"

echo [start-all] Starting pump sidecar...
start "pump-ws-sidecar" cmd /k "%~dp0start-pump-ws-sidecar.bat"

echo [start-all] All processes launched in separate windows.
