@echo off
chcp 65001 >nul
cd /d "%~dp0"
title CustomRecorder dev agent
echo CustomRecorder 개발용 자동 빌드 도우미입니다. 작업이 끝나면 이 창을 닫으세요.
if not exist .tooling mkdir .tooling
:loop
if exist .tooling\build.req (
  del .tooling\build.req
  echo [%time%] build
  call build.bat
)
if exist .tooling\run.req (
  del .tooling\run.req
  echo [%time%] run --visible
  start "" "src\CustomRecorder.App\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\CustomRecorder.exe" --visible
)
if exist .tooling\kill.req (
  del .tooling\kill.req
  echo [%time%] kill
  taskkill /im CustomRecorder.exe /f >nul 2>&1
)
timeout /t 1 /nobreak >nul
goto loop
