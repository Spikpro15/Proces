@echo off
chcp 65001 >nul
setlocal
set "TOOLS_DIR=%~dp0tools"
if not exist "%TOOLS_DIR%" mkdir "%TOOLS_DIR%"

echo [1/2] Проверка Android Platform-Tools (ADB)...
if not exist "%TOOLS_DIR%\platform-toolsdb.exe" (
    echo Скачиваю официальный Android Platform-Tools...
    powershell -NoProfile -Command "Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile '%TOOLS_DIR%\platform-tools.zip'"
    powershell -NoProfile -Command "Expand-Archive -Path '%TOOLS_DIR%\platform-tools.zip' -DestinationPath '%TOOLS_DIR%' -Force"
    del "%TOOLS_DIR%\platform-tools.zip"
) else (
    echo ADB уже установлен.
)

echo [2/2] Проверка Intel PresentMon (автономный FPS)...
if not exist "%TOOLS_DIR%\PresentMon.exe" (
    echo Скачиваю последний релиз Intel PresentMon с GitHub...
    powershell -NoProfile -Command ^
      "$rel = Invoke-RestMethod 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest'; ^
       $asset = $rel.assets | Where-Object { $_.name -like 'PresentMon-*.exe' } | Select-Object -First 1; ^
       if ($asset) { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile '%TOOLS_DIR%\PresentMon.exe'; Write-Host ('Скачан ' + $asset.name) } else { Write-Host 'Не удалось найти PresentMon-*.exe автоматически, скачай вручную с https://github.com/GameTechDev/PresentMon/releases' }"
) else (
    echo PresentMon уже установлен.
)

echo.
echo Готово! Инструменты лежат в папке tools\.
pause
