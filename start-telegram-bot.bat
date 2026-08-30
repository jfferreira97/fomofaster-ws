@echo off
REM Drop this file at the ROOT of the cloned fomofaster-ws repo (sibling to telegram-bot\)
REM Backend API — listens on http://0.0.0.0:8000 (ws-sidecar posts to this)
cd /d "%~dp0telegram-bot\TelegramBot"
dotnet run
