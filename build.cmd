@echo off
rem Build InputMethodLock.exe (.NET Framework 4.8, no runtime dependency)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=bin\InputMethodLock.exe

if not exist bin mkdir bin

"%CSC%" /nologo /target:winexe /platform:anycpu /out:%OUT% /optimize+ /win32icon:src\app.ico /win32manifest:src\app.manifest /res:src\icon.png,InputMethodLock.icon.png /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\Program.cs src\ImeApi.cs src\ImeLocker.cs src\ImeWatcher.cs src\TsfProfiles.cs src\Logger.cs src\HotkeyHook.cs src\TrayIcon.cs src\SettingsForm.cs src\Config.cs src\AutoStart.cs

if %errorlevel%==0 (
  echo Build OK: %OUT%
) else (
  echo Build FAILED.
  exit /b 1
)
