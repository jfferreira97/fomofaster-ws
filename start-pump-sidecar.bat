@echo off
REM Drop this file at the ROOT of the cloned fomofaster-ws repo (sibling to pump-sidecar\)
REM Requires: npm install && npx playwright install chrome   (run once, inside pump-sidecar\)
REM First run only: a visible Chrome window opens to pump.fun and waits up to 5 min
REM for you to log in by hand. That session then persists in pump-sidecar\chromium-profile\
REM and every run after this one can be headless (set HEADLESS=true below).
REM Start the backend (start-telegram-bot.bat) FIRST — this posts to it on :8000.
set "PATH=C:\Program Files\nodejs;%PATH%"
cd /d "%~dp0pump-sidecar"
npm start
