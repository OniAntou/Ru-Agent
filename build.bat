@echo off
setlocal enabledelayedexpansion
echo ============================================
echo  Ru Desktop - Build + PRI Generation Script
echo  .NET 10 + WinUI 3 (Unpackaged Self-Contained)
echo ============================================
echo.

set "PROJECT_DIR=%~dp0gui-winui3"
set "CSPROJ=%PROJECT_DIR%\RuApp.csproj"
set "PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
set "OBJ_DIR=%PROJECT_DIR%\obj\Release\net10.0-windows10.0.26100.0\win-x64"
set "STAGING_DIR=%PROJECT_DIR%\pri_staging"
set "MAKEPRI=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe"

echo [1/5] Cleaning previous build...
dotnet clean "%CSPROJ%" -r win-x64 -c Release >nul 2>&1

echo [2/5] Publishing Release (self-contained, win-x64)...
dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo BUILD FAILED!
    pause
    exit /b 1
)

echo [3/5] Copying compiled XAML (.xbf) files...
if not exist "%PUBLISH_DIR%\Views" mkdir "%PUBLISH_DIR%\Views"
copy /Y "%OBJ_DIR%\App.xbf" "%PUBLISH_DIR%\" >nul 2>&1
copy /Y "%OBJ_DIR%\MainWindow.xbf" "%PUBLISH_DIR%\" >nul 2>&1
for %%f in ("%OBJ_DIR%\Views\*.xbf") do (
    copy /Y "%%f" "%PUBLISH_DIR%\Views\" >nul 2>&1
)

echo [4/5] Skipping manual resources.pri generation (relying on MSBuild)

if exist "%PUBLISH_DIR%\resources.pri" (
    echo    resources.pri created successfully!
) else (
    echo    WARNING: resources.pri was NOT created. App may not start.
    pause
    exit /b 1
)

echo [5/5] Build complete!
echo.
echo ============================================
echo  Output: %PUBLISH_DIR%\RuApp.exe
echo  Launch: start_dev.bat
echo ============================================
echo.
