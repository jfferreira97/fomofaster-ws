@echo off
REM Lives in scripts\ — sibling to fomo-ws-sidecar\ is one level up.
REM Requires: npm install && npx playwright install chrome   (run once, see SETUP.txt)
REM Requires: chromium-profile\ restored from PROD BEFORE first run, or you'll have
REM to re-do the manual fomo.family login (5 min window on first launch).
REM Start the backend (start-telegram-bot.bat) FIRST — this posts to it on :8000.
set "PATH=C:\Program Files\nodejs;%PATH%"
cd /d "%~dp0..\fomo-ws-sidecar"
npm start
