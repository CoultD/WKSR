@echo off
rem ==== Play the bot (local test) ====
rem Starts a local match server and a bot that keeps queueing on it. Nothing here touches the live server.
rem Build first so dist\ has the programs:  powershell -ExecutionPolicy Bypass -File tools\build.ps1
rem Point your game at this server: in BepInEx\config\com.wkrace.mod.cfg, under [Debug], set
rem   TestServer = 127.0.0.1:47601
rem   Profile = bot          (a separate player file, so your real one is left alone)
rem and clear both again afterwards. Your game needs the WK Race mod of the same version as this build.

rem ---- settings ---------------------------------------------------------------
rem How fast the bot climbs (metres per second) and when it finishes (seconds after GO).
set BOT_NAME=Pacer
set BOT_SPEED=0.7
set BOT_FINISH=900
rem A port of its own, so this never clashes with another server on this PC.
set PORT=47601
rem -----------------------------------------------------------------------------

setlocal
title WK Race - play the bot
set "DIST=%~dp0..\dist"
set "SERVER=%DIST%\server\WKRace.Server.exe"
set "BOT=%DIST%\devtools\WKRace.DevTools.exe"
set "DATA=%APPDATA%\WKRace\bot-server"

for %%F in ("%SERVER%" "%BOT%") do (
    if not exist "%%~F" (
        echo Missing %%~F
        echo Build first:  powershell -ExecutionPolicy Bypass -File tools\build.ps1
        pause
        exit /b 1
    )
)

echo Starting the local server on port %PORT% ...
start "WK Race local server" "%SERVER%" --port %PORT% --data "%DATA%"
timeout /t 2 /nobreak >nul

echo Starting the bot (%BOT_NAME%, %BOT_SPEED% m/s, finishes after %BOT_FINISH% s) ...
start "WK Race bot" "%BOT%" bot --server 127.0.0.1:%PORT% --name %BOT_NAME% --speed %BOT_SPEED% --finish-after %BOT_FINISH% --loop

echo.
echo In the game open WKSR from the main menu and press RANKED, then 1V1, to play %BOT_NAME%.
echo Close the server and bot windows when you are done.
timeout /t 8 >nul
