@echo off
cd /d "%~dp0"
echo === Sound-to-Vibration (grips only). Default speaker game audio -^> Xbox grips. ===
echo Keep window OPEN. Default playback should be your REAL speakers.
echo.
"bin\Release\net8.0-windows10.0.19041.0\ps5r2x.exe" --source auto --gain 3 --smooth 0.35
echo.
echo Exited. Motors zeroed.
pause