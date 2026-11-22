@echo off
rem Сборка BatteryTray компилятором, встроенным в Windows (.NET Framework 4.x).
rem Ничего устанавливать не нужно. Результат: bin\BatteryTray.exe
setlocal
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (echo [ERROR] csc.exe not found - .NET Framework 4.x is required & exit /b 1)

set "WMD=%SystemRoot%\System32\WinMetadata"
if not exist "%WMD%\Windows.Devices.winmd" (echo [ERROR] %WMD%\Windows.Devices.winmd not found & exit /b 1)

set "GAC=%SystemRoot%\Microsoft.NET\assembly\GAC_MSIL"
call :findref RWRT   System.Runtime.WindowsRuntime                    || exit /b 1
call :findref SRT    System.Runtime                                   || exit /b 1
call :findref SRTIWR System.Runtime.InteropServices.WindowsRuntime    || exit /b 1
call :findref SOM    System.ObjectModel                               || exit /b 1
call :findref STT    System.Threading.Tasks                           || exit /b 1

if not exist bin mkdir bin

"%CSC%" /nologo /codepage:65001 /utf8output /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /out:"bin\BatteryTray.exe" ^
  /win32icon:"assets\app.ico" ^
  /win32manifest:"src\app.manifest" ^
  /r:"%RWRT%" /r:"%SRT%" /r:"%SRTIWR%" /r:"%SOM%" /r:"%STT%" ^
  /r:"%WMD%\Windows.Foundation.winmd" ^
  /r:"%WMD%\Windows.Devices.winmd" ^
  /r:"%WMD%\Windows.Storage.winmd" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /recurse:"src\*.cs"
if errorlevel 1 (echo. & echo BUILD FAILED & exit /b 1)

echo BUILD OK: %~dp0bin\BatteryTray.exe
endlocal
exit /b 0

rem :findref VAR AssemblyName, ищет фасадную сборку в GAC_MSIL
:findref
set "%1="
for /f "delims=" %%i in ('dir /b /s "%GAC%\%2\%2.dll" 2^>nul') do set "%1=%%i"
if not defined %1 (echo [ERROR] %2.dll not found in %GAC% & exit /b 1)
exit /b 0
