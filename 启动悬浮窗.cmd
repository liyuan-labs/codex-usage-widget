@echo off
setlocal
chcp 65001 >nul
set "ROOT=%~dp0"
set "EXE=%ROOT%artifacts\publish\CodexUsageWidget.exe"
set "NEEDS_BUILD=0"

if not exist "%EXE%" (
  set "NEEDS_BUILD=1"
) else (
  powershell.exe -NoProfile -Command "$exe=Get-Item -LiteralPath '%EXE%'; if (Get-ChildItem -LiteralPath '%ROOT%src' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $exe.LastWriteTimeUtc } | Select-Object -First 1) { exit 10 }"
  if errorlevel 1 set "NEEDS_BUILD=1"
)

if "%NEEDS_BUILD%"=="1" (
  echo 检测到首次启动或源码更新，正在构建悬浮窗...
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
