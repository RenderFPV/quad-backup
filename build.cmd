@echo off
rem Builds QuadBackup.exe with the C# compiler that ships with Windows (.NET Framework 4.x). Nothing to install.
rem Exit Quad Backup from its tray icon first, or the old exe will be locked.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set ICON=
if exist "%~dp0src\quad.ico" set ICON=/win32icon:"%~dp0src\quad.ico"
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%~dp0QuadBackup.exe" /win32manifest:"%~dp0src\app.manifest" %ICON% ^
  /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 (echo Build failed. & exit /b 1)
echo Built QuadBackup.exe
