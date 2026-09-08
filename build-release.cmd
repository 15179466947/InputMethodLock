@echo off
rem Full release pipeline:
rem   1) build exe (build.cmd)
rem   2) assemble self-contained release\ folder (exe is zero-dependency on .NET Framework 4.8)
rem   3) build installer with Inno Setup 6 if available
setlocal
cd /d "%~dp0"

echo [1/3] Building exe...
call build.cmd
if errorlevel 1 (
  echo Build FAILED, aborting release.
  exit /b 1
)

echo.
echo [2/3] Assembling release folder...
if exist release rmdir /s /q release
mkdir release
copy /y bin\InputMethodLock.exe release\ >nul
copy /y README.md release\ >nul
echo   release\InputMethodLock.exe
echo   release\README.md

echo.
echo [3/3] Building installer...
set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe
if exist "%ISCC%" (
  "%ISCC%" packaging\InputMethodLock.iss
  if errorlevel 1 (
    echo Installer build FAILED.
    exit /b 1
  )
  echo.
  echo Installer: dist\InputMethodLock-1.0.0-setup.exe
) else (
  echo Inno Setup 6 not found - release folder is ready without installer.
  echo Install Inno Setup, then re-run this script:
  echo   winget install -e --id JRSoftware.InnoSetup
  exit /b 0
)

echo.
echo Release OK.
