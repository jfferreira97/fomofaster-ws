@echo off
REM Drop this file at the ROOT of the cloned fomofaster-ws repo.
REM Launches backend first, waits for it to come up, then the sidecar. Each in its own window.
start "TelegramBot" cmd /k "%~dp0start-telegram-bot.bat"
echo Waiting 10s for backend to bind :8000...
timeout /t 10 /nobreak >nul
start "ws-sidecar" cmd /k "%~dp0start-ws-sidecar.bat"
