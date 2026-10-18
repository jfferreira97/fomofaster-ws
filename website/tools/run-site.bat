@echo off
title groupchat-site-sync
cd /d %~dp0
:loop
node build.js --watch
echo [%date% %time%] site sync exited, restarting in 10s...
timeout /t 10 /nobreak >nul
goto loop
