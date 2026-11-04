@echo off
rem Rebuilds Redux.exe using the C# compiler that is already inside Windows (no installs needed).
rem Keep logo.png and icon.ico next to this file.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:Redux.exe /win32manifest:app.manifest /win32icon:icon.ico /resource:logo.png,logo.png /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll Program.cs
if errorlevel 1 (echo BUILD FAILED) else (echo Built Redux.exe)
pause
