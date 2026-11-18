@echo off
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319

rem 1) program
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 ^
  /out:"%~dp0DiskBar.exe" /win32manifest:"%~dp0app.manifest" ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0DiskBar.cs"
if errorlevel 1 (echo BLAD kompilacji DiskBar & exit /b 1)
echo OK: %~dp0DiskBar.exe

rem 2) instalator z wbudowanym DiskBar.exe
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 ^
  /out:"%~dp0DiskBarSetup.exe" /resource:"%~dp0DiskBar.exe",DiskBar.exe ^
  /r:System.Windows.Forms.dll "%~dp0Setup.cs"
if errorlevel 1 (echo BLAD kompilacji instalatora & exit /b 1)
echo OK: %~dp0DiskBarSetup.exe
