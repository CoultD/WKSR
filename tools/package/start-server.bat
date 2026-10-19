@echo off
rem ==== White Knuckle Race server ====
rem Only needed for WK Race clients older than 0.3.0: the address friends reach this PC on,
rem for example:  set PUBLIC_ADDRESS=203.0.113.7:47600   (your public IP, a Radmin VPN IP or a playit.gg address)
set PUBLIC_ADDRESS=

title WK Race server
set "SERVER=%~dp0WKRace.Server.exe"
if not exist "%SERVER%" (
    echo WKRace.Server.exe was not found next to this file.
    echo Run start-server.bat from the unzipped WKRace-Server folder.
    pause
    exit /b 1
)
if "%PUBLIC_ADDRESS%"=="" (
    "%SERVER%" --port 47600 --data "%~dp0data"
) else (
    "%SERVER%" --port 47600 --data "%~dp0data" --public-address %PUBLIC_ADDRESS%
)
echo.
echo The server stopped.
pause