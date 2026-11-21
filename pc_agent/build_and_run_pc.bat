@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

if not exist "tools\platform-toolsdb.exe" call setup_tools.bat
if not exist "tools\PresentMon.exe" call setup_tools.bat

echo Сборка PCMonitorAgent (.NET 8)...
dotnet build -c Release -o dist
if errorlevel 1 (
    echo.
    echo Ошибка сборки! Убедись, что установлен .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

if not exist "dist	ools" mkdir "dist	ools"
xcopy /E /I /Y "tools" "dist	ools" >nul

echo Запуск PCMonitorAgent (потребуются права администратора для температуры CPU и FPS)...
start "" "dist\PCMonitorAgent.exe"
