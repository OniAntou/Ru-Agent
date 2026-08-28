@echo off
setlocal
echo Starting Ru Native WinUI 3 Desktop App (.NET 10 x64)...
set "EXE=%~dp0gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe"
if not exist "%EXE%" (
    echo Release publish output not found. Run build.bat and wait for [12/12] Build complete.
    echo Expected: "%EXE%"
    endlocal
    exit /b 1
)
for %%I in ("%EXE%") do echo Published: %%~tI
echo Executable: "%EXE%"
start "" "%EXE%"
endlocal
exit /b 0
