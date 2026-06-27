@echo off
:: Restart TeslaPC server - right-click and Run as Administrator
taskkill /F /IM TeslaPCInterface.exe >nul 2>&1
timeout /t 2 /nobreak >nul
cd /d "%~dp0bin\Release\net6.0-windows"
if not exist TeslaPCInterface.exe (
    echo Build first: dotnet build TeslaPCInterface.sln -c Release
    pause
    exit /b 1
)
start "TeslaPC" "%~dp0bin\Release\net6.0-windows\TeslaPCInterface.exe" --no-tesla-bypass
echo.
echo Started. Open https://localhost:8443/
echo Watch the TeslaPC console for lines like: [HTTP] GET /
pause