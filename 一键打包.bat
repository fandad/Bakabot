@echo off
chcp 65001 >nul
title Bakabot 一键打包

echo ============================================
echo   Bakabot 一键打包 (Release)
echo ============================================
echo.

dotnet build "%~dp0Bakabot\Bakabot.csproj" --configuration Release --nologo

if %errorlevel% neq 0 (
    echo.
    echo [失败] 构建出错。若提示文件被占用，请先关闭正在运行的 Bakabot.exe 再重试。
    pause
    exit /b 1
)

echo.
echo ============================================
echo [成功] 产物位置:
echo %~dp0Bakabot\bin\Release\net8.0-windows\Bakabot.exe
echo ============================================
pause
