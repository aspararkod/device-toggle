@echo off
rem Builds dist\DeviceToggle.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK or Visual Studio needed.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find csc.exe from .NET Framework 4.x.
    exit /b 1
)

if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
    /out:dist\DeviceToggle.exe ^
    /win32manifest:app.manifest /win32icon:app.ico ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    src\DeviceToggle.cs
if errorlevel 1 exit /b 1

echo Built dist\DeviceToggle.exe
