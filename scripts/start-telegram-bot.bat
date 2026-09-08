@echo off
REM Lives in scripts\ — sibling to telegram-bot\ is one level up.
REM Backend API — listens on http://0.0.0.0:8000 (fomo-ws-sidecar posts to this)
cd /d "%~dp0..\telegram-bot\TelegramBot"
dotnet run
