@echo off
setlocal
echo Starting Ru Native WinUI 3 Desktop App (.NET 10 x64)...
set "EXE=%~dp0gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe"
set "WORKER_EXE=%~dp0backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe"
set "LAUNCHER=%~dp0scripts\launch_gui.ps1"
if not exist "%EXE%" (
    echo Release publish output not found. Run build.bat and wait for [13/13] Build complete.
    echo Expected: "%EXE%"
    endlocal
    exit /b 1
)
if not exist "%WORKER_EXE%" (
    echo Managed Worker publish output not found. Run build.bat and wait for [13/13] Build complete.
    echo Expected: "%WORKER_EXE%"
    endlocal
    exit /b 1
)
if not exist "%LAUNCHER%" (
    echo GUI launcher helper not found. Expected: "%LAUNCHER%"
    endlocal
    exit /b 1
)
for %%I in ("%EXE%") do echo Published: %%~tI
echo Executable: "%EXE%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%LAUNCHER%" -ExecutablePath "%EXE%" -WorkingDirectory "%~dp0." -StartupGracePeriodMilliseconds 2000
if errorlevel 1 (
    echo.
    echo RuApp failed during startup. Check the Windows Application event log or rebuild with build.bat.
    pause
    endlocal
    exit /b 1
)
endlocal
exit /b 0
