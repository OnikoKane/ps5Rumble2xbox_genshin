@echo off
cd /d "%~dp0"
echo === DualSense NATIVE mode (capture virtual DualSense 4ch haptics) ===
echo Keep window OPEN. For audio: set Windows default playback = DualSense, OR keep Realtek (audio direct).
echo ch3/4 haptics -^> Xbox motors. If Genshin shows Xbox (not DualSense) there may be no ch3/4.
echo.
"bin\Release\net8.0-windows10.0.19041.0\ps5r2x.exe" --run --triggers
echo.
echo Exited. Motors zeroed.
pause