@echo off
setlocal enabledelayedexpansion
echo ============================================
echo  Ru Desktop - Build + PRI Generation Script
echo  .NET 10 + ASP.NET Core + WinUI 3 (Unpackaged Self-Contained)
echo ============================================
echo.

set "PROJECT_DIR=%~dp0gui-winui3"
set "CSPROJ=%PROJECT_DIR%\RuApp.csproj"
set "BACKEND_PROJECT=%~dp0backend-dotnet\RuBackend\RuBackend.csproj"
set "BACKEND_PUBLISH_DIR=%~dp0backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish"
set "PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
set "OBJ_DIR=%PROJECT_DIR%\obj\Release\net10.0-windows10.0.26100.0\win-x64"
set "STAGING_DIR=%PROJECT_DIR%\pri_staging"
set "MAKEPRI=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe"

echo [1/6] Cleaning previous build...
dotnet clean "%CSPROJ%" -r win-x64 -c Release >nul 2>&1

echo [2/6] Publishing ASP.NET Core backend (self-contained, win-x64)...
dotnet publish "%BACKEND_PROJECT%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo BACKEND BUILD FAILED!
    pause
    exit /b 1
)
if not exist "%BACKEND_PUBLISH_DIR%\RuBackend.exe" (
    echo.
    echo BACKEND BUILD FAILED: RuBackend.exe was not created.
    pause
    exit /b 1
)

echo [3/6] Publishing Release GUI (self-contained, win-x64)...
dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo BUILD FAILED!
    pause
    exit /b 1
)

echo [4/6] Copying compiled XAML (.xbf) files...
if not exist "%PUBLISH_DIR%\Views" mkdir "%PUBLISH_DIR%\Views"
copy /Y "%OBJ_DIR%\App.xbf" "%PUBLISH_DIR%\" >nul 2>&1
copy /Y "%OBJ_DIR%\MainWindow.xbf" "%PUBLISH_DIR%\" >nul 2>&1
for %%f in ("%OBJ_DIR%\Views\*.xbf") do (
    copy /Y "%%f" "%PUBLISH_DIR%\Views\" >nul 2>&1
)

echo [5/6] Skipping manual resources.pri generation (relying on MSBuild)

if exist "%PUBLISH_DIR%\RuApp.pri" (
    echo    RuApp.pri created successfully!
) else (
    echo    WARNING: RuApp.pri was NOT created. App may not start.
    pause
    exit /b 1
)

echo [6/6] Build complete!
echo.
echo ============================================
echo  Backend: %BACKEND_PUBLISH_DIR%\RuBackend.exe
echo  Output:  %PUBLISH_DIR%\RuApp.exe
echo  Launch: start_dev.bat
echo ============================================
echo.
