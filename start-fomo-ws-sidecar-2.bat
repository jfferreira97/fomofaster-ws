@echo off
REM Second fomo.family account/session — separate followings, separate persistent
REM Chrome profile, runs concurrently with start-fomo-ws-sidecar.bat (the first account).
REM To add a 3rd/4th account: copy this file, bump the profile dir + account name below.
REM First run on a fresh profile pops up a visible Chrome — log in by hand within 5 min.
REM Start the backend (start-telegram-bot.bat) FIRST — this posts to it on :8000.
set "PATH=C:\Program Files\nodejs;%PATH%"
set "PROFILE_DIR=./chromium-profile-2"
set "ACCOUNT_NAME=account-2"
cd /d "%~dp0fomo-ws-sidecar"
npm start
