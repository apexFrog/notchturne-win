@echo off
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if errorlevel 1 (
  echo Build failed
  pause
  exit /b 1
)
echo.
echo Done: dist\NotchturneWin.exe
explorer dist
pause
