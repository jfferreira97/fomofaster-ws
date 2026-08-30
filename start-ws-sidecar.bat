@echo off
REM Drop this file at the ROOT of the cloned fomofaster-ws repo (sibling to ws-sidecar\)
REM Requires: npm install && npx playwright install chrome   (run once, see SETUP.txt)
REM Requires: chromium-profile\ restored from PROD BEFORE first run, or you'll have
REM to re-do the manual fomo.family login (5 min window on first launch).
REM Start the backend (start-telegram-bot.bat) FIRST — this posts to it on :8000.
cd /d "%~dp0ws-sidecar"
npm start
