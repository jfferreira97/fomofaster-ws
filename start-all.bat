@echo off
REM Drop this file at the ROOT of the cloned fomofaster-ws repo.
REM Launches backend first, waits for it to come up, then both sidecars. Each in its own window.
REM NOTE: on pump-sidecar's very first run ever, its window pops up a visible Chrome
REM pointed at pump.fun and waits up to 5 min for you to log in by hand. After that
REM one-time login the session persists and every future start-all.bat run is silent.
start "TelegramBot" cmd /k "%~dp0start-telegram-bot.bat"
echo Waiting 10s for backend to bind :8000...
timeout /t 10 /nobreak >nul
start "ws-sidecar" cmd /k "%~dp0start-ws-sidecar.bat"
start "pump-sidecar" cmd /k "%~dp0start-pump-sidecar.bat"
