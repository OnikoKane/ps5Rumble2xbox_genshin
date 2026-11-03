@echo off
cd /d "%~dp0"
echo === ps5Rumble2Xbox : AudioHaptics to Real Xbox 4 motors ===
echo Hold your Xbox pad. Grips AND triggers buzz about 25 seconds.
echo.
"bin\Release\net8.0-windows10.0.19041.0\ps5r2x.exe" --demo --endpoint "DualSense" --triggers --gain 8 --smooth 0.2 --duration 25
echo.
echo Done. If grips and triggers buzzed = conversion OK.
pause