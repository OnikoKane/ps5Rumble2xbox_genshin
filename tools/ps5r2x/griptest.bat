@echo off
cd /d "%~dp0"
echo === GRIP ONLY test: only Left/Right grips should buzz (no triggers) ===
echo Hold your Xbox pad ~20s and feel the two GRIPS.
echo.
"bin\Release\net8.0-windows10.0.19041.0\ps5r2x.exe" --demo --endpoint "DualSense" --gain 8 --smooth 0.2 --duration 20
echo.
echo Done.
pause