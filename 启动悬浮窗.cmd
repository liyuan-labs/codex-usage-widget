@echo off
setlocal
chcp 65001 >nul
set "ROOT=%~dp0"
set "EXE=%ROOT%artifacts\publish\CodexUsageWidget.exe"

if not exist "%EXE%" (
  echo 首次启动，正在构建悬浮窗...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%发布.ps1"
  if errorlevel 1 (
    echo.
    echo 构建失败。请确认已安装 .NET 8 Desktop Runtime/SDK。
    pause
    exit /b 1
  )
)

start "" "%EXE%"
exit /b 0
