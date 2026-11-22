@echo off
if not exist "%~dp0dist\NotchturneWin.exe" (
  echo Run build.bat first
  pause
  exit /b 1
)
powershell -NoProfile -Command "$s=(New-Object -COM WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Startup')+'\NotchturneWin.lnk'); $s.TargetPath='%~dp0dist\NotchturneWin.exe'; $s.WorkingDirectory='%~dp0dist'; $s.Save()"
echo Autostart enabled
pause
