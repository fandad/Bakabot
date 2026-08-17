@echo off
chcp 65001 >nul
title Bakabot 发布独立版

echo ============================================
echo   Bakabot 发布独立单文件版 (win-x64)
echo   产物为单个 exe，无需安装 .NET，可直接分发
echo ============================================
echo.

dotnet publish "%~dp0Bakabot\Bakabot.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true --nologo

if %errorlevel% neq 0 (
    echo.
    echo [失败] 发布出错。若提示文件被占用，请先关闭正在运行的 Bakabot.exe 再重试。
    pause
    exit /b 1
)

echo.
echo ============================================
echo [成功] 独立版位置(单个 exe 即可直接拷走使用):
echo %~dp0Bakabot\bin\Release\net8.0-windows\win-x64\publish\Bakabot.exe
echo ============================================
pause
