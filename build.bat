@echo off
setlocal enabledelayedexpansion
echo ============================================
echo  Ru Desktop - Build + PRI Generation Script
echo  .NET 10 + ASP.NET Core + WinUI 3 (Unpackaged Self-Contained)
echo ============================================
echo.

set "PROJECT_DIR=%~dp0gui-winui3"
set "CSPROJ=%PROJECT_DIR%\RuApp.csproj"
set "BROKER_PROJECT=%~dp0backend-dotnet\RuAgent.Broker\RuAgent.Broker.csproj"
set "BROKER_PUBLISH_DIR=%~dp0backend-dotnet\RuAgent.Broker\bin\Release\net10.0\win-x64\publish"
set "BACKEND_PROJECT=%~dp0backend-dotnet\RuBackend\RuBackend.csproj"
set "BACKEND_PUBLISH_DIR=%~dp0backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish"
set "PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
set "OBJ_DIR=%PROJECT_DIR%\obj\Release\net10.0-windows10.0.26100.0\win-x64"

echo [1/7] Cleaning previous build...
call dotnet clean "%CSPROJ%" -r win-x64 -c Release >nul 2>&1
if errorlevel 1 (
    echo CLEAN FAILED!
    exit /b 1
)

echo [2/7] Publishing RuAgentBroker (self-contained, win-x64)...
call dotnet publish "%BROKER_PROJECT%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo BROKER BUILD FAILED!
    exit /b 1
)
if not exist "%BROKER_PUBLISH_DIR%\RuAgent.Broker.exe" (
    echo.
    echo BROKER BUILD FAILED: RuAgent.Broker.exe was not created.
    exit /b 1
)

echo [3/7] Publishing ASP.NET Core backend (self-contained, win-x64)...
call dotnet publish "%BACKEND_PROJECT%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo BACKEND BUILD FAILED!
    exit /b 1
)
if not exist "%BACKEND_PUBLISH_DIR%\RuBackend.exe" (
    echo.
    echo BACKEND BUILD FAILED: RuBackend.exe was not created.
    exit /b 1
)

echo [4/7] Publishing Release GUI (self-contained, win-x64)...
call dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo BUILD FAILED!
    exit /b 1
)

echo [5/7] Copying compiled XAML (.xbf) files...
if not exist "%PUBLISH_DIR%\Views" mkdir "%PUBLISH_DIR%\Views"
copy /Y "%OBJ_DIR%\App.xbf" "%PUBLISH_DIR%\" >nul 2>&1
copy /Y "%OBJ_DIR%\MainWindow.xbf" "%PUBLISH_DIR%\" >nul 2>&1
for %%f in ("%OBJ_DIR%\Views\*.xbf") do (
    copy /Y "%%f" "%PUBLISH_DIR%\Views\" >nul 2>&1
)

echo [6/7] Building and testing native desktop overlay...
call powershell -ExecutionPolicy Bypass -File "%~dp0scripts\build_ru_desktop_overlay.ps1"
if errorlevel 1 (
    echo.
    echo NATIVE OVERLAY BUILD FAILED!
    exit /b 1
)
if not exist "%~dp0tools\ru_desktop_overlay\out\build\Release\RuDesktopOverlay.exe" (
    echo.
    echo NATIVE OVERLAY BUILD FAILED: RuDesktopOverlay.exe was not created.
    exit /b 1
)

echo [7/7] Build complete!
echo.
echo ============================================
echo  Broker:  %BROKER_PUBLISH_DIR%\RuAgent.Broker.exe
echo  Backend: %BACKEND_PUBLISH_DIR%\RuBackend.exe
echo  Output:  %PUBLISH_DIR%\RuApp.exe
echo  Launch: start_dev.bat
echo ============================================
echo.
