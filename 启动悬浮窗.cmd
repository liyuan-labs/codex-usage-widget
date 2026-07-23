@echo off
setlocal
chcp 65001 >nul
set "ROOT=%~dp0"
set "EXE=%ROOT%artifacts\publish\CodexUsageWidget.exe"

if not exist "%EXE%" goto build

powershell.exe -NoProfile -Command "$exe=Get-Item -LiteralPath '%EXE%'; if (Get-ChildItem -LiteralPath '%ROOT%src' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $exe.LastWriteTimeUtc } | Select-Object -First 1) { exit 10 }"
if errorlevel 10 goto build
goto launch

:build
echo 检测到首次启动或源码更新，正在构建悬浮窗...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%发布.ps1"
if errorlevel 1 goto build_failed

:launch
start "" "%EXE%"
exit /b 0

:build_failed
echo.
echo 构建失败。请确认已安装 .NET 8 Desktop Runtime/SDK。
pause
exit /b 1
