@echo off
echo Starting Ru Native WinUI 3 Desktop App (.NET 10 x64)...
set "EXE=%~dp0gui-winui3\bin\Debug\net10.0-windows10.0.26100.0\win-x64\RuApp.exe"
if exist "%EXE%" (
    start "" "%EXE%"
) else (
    echo RuApp.exe not found. Run build.bat first.
    echo Expected: %EXE%
    pause
)
