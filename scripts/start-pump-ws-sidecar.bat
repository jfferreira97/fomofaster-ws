@echo off
REM Lives in scripts\ — sibling to pump-ws-sidecar\ is one level up.
REM Requires: npm install && npx playwright install chrome   (run once, inside pump-ws-sidecar\)
REM First run only: a visible Chrome window opens to pump.fun and waits up to 5 min
REM for you to log in by hand. That session then persists in pump-ws-sidecar\chromium-profile\
REM and every run after this one can be headless (set HEADLESS=true below).
REM Start the backend (start-telegram-bot.bat) FIRST — this posts to it on :8000.
set "PATH=C:\Program Files\nodejs;%PATH%"
cd /d "%~dp0..\pump-ws-sidecar"
npm start
