@echo off
setlocal enabledelayedexpansion
echo ============================================
echo  Ru Desktop - Build + PRI Generation Script
echo  .NET 10 + ASP.NET Core + WinUI 3 (Unpackaged Self-Contained)
echo  Coding core: 62 HTTP + 1 WS, managed C# Worker, CLI tracer
echo ============================================
echo.

set "PROJECT_DIR=%~dp0gui-winui3"
set "CSPROJ=%PROJECT_DIR%\RuApp.csproj"
set "HARNESS_CORE_PROJECT=%~dp0backend-dotnet\RuAgent.Harness.Core\RuAgent.Harness.Core.csproj"
set "HARNESS_PERSISTENCE_PROJECT=%~dp0backend-dotnet\RuAgent.Harness.Persistence\RuAgent.Harness.Persistence.csproj"
set "HOSTING_PROJECT=%~dp0backend-dotnet\RuAgent.Hosting\RuAgent.Hosting.csproj"
set "CLIENT_PROJECT=%~dp0backend-dotnet\RuAgent.Client\RuAgent.Client.csproj"
set "CLI_PROJECT=%~dp0backend-dotnet\RuAgent.Cli\RuAgent.Cli.csproj"
set "CLI_PUBLISH_DIR=%~dp0backend-dotnet\RuAgent.Cli\bin\Release\net10.0\win-x64\publish"
set "WORKER_PROJECT=%~dp0backend-dotnet\RuAgent.Runtime.Worker\RuAgent.Runtime.Worker.csproj"
set "WORKER_PUBLISH_DIR=%~dp0backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish"
set "RUNTIME_CORE_PUBLISH_DLL=%WORKER_PUBLISH_DIR%\RuAgent.Runtime.Core.dll"
set "BROKER_PROJECT=%~dp0backend-dotnet\RuAgent.Broker\RuAgent.Broker.csproj"
set "BROKER_PUBLISH_DIR=%~dp0backend-dotnet\RuAgent.Broker\bin\Release\net10.0\win-x64\publish"
set "BACKEND_PROJECT=%~dp0backend-dotnet\RuBackend\RuBackend.csproj"
set "BACKEND_PUBLISH_DIR=%~dp0backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish"
set "PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
set "OBJ_DIR=%PROJECT_DIR%\obj\x64\Release\net10.0-windows10.0.26100.0\win-x64"

echo [1/12] Cleaning previous build...
call dotnet clean "%CSPROJ%" -r win-x64 -c Release -p:Platform=x64
if errorlevel 1 (
    echo CLEAN FAILED!
    exit /b 1
)

echo [2/12] Building RuAgent.Harness.Core (deterministic, BCL-only, no deps)...
call dotnet build "%HARNESS_CORE_PROJECT%" -c Release --nologo
if errorlevel 1 (
    echo.
    echo HARNESS CORE BUILD FAILED!
    exit /b 1
)

echo [3/12] Building RuAgent.Harness.Persistence (Core + Microsoft.Data.Sqlite)...
call dotnet build "%HARNESS_PERSISTENCE_PROJECT%" -c Release --nologo
if errorlevel 1 (
    echo.
    echo HARNESS PERSISTENCE BUILD FAILED!
    exit /b 1
)

echo [4/12] Building RuAgent.Hosting (Worker/Broker Job Object + runtime descriptor)...
call dotnet build "%HOSTING_PROJECT%" -c Release --nologo
if errorlevel 1 (
    echo.
    echo HOSTING BUILD FAILED!
    exit /b 1
)

echo [5/12] Building RuAgent.Client (PublicHost start-or-attach + API/SSE clients)...
call dotnet build "%CLIENT_PROJECT%" -c Release --nologo
if errorlevel 1 (
    echo.
    echo CLIENT BUILD FAILED!
    exit /b 1
)

echo [6/12] Publishing RuAgent.Cli headless tracer (self-contained, win-x64)...
call dotnet publish "%CLI_PROJECT%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo CLI BUILD FAILED!
    exit /b 1
)
if not exist "%CLI_PUBLISH_DIR%\RuAgent.Cli.exe" (
    echo.
    echo CLI BUILD FAILED: RuAgent.Cli.exe was not created.
    exit /b 1
)

echo [7/12] Publishing managed Worker v1/v2 plus Runtime.Core (self-contained, win-x64)...
call dotnet publish "%WORKER_PROJECT%" -c Release -r win-x64 --self-contained true
if errorlevel 1 (
    echo.
    echo MANAGED WORKER BUILD FAILED!
    exit /b 1
)
if not exist "%WORKER_PUBLISH_DIR%\RuAgent.Runtime.Worker.exe" (
    echo.
    echo MANAGED WORKER BUILD FAILED: RuAgent.Runtime.Worker.exe was not created.
    exit /b 1
)
if not exist "%RUNTIME_CORE_PUBLISH_DLL%" (
    echo.
    echo MANAGED WORKER BUILD FAILED: RuAgent.Runtime.Core.dll was not published.
    exit /b 1
)

echo [8/12] Publishing RuAgentBroker (self-contained, win-x64)...
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

echo [9/12] Publishing ASP.NET Core backend (self-contained, win-x64)...
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

echo [10/12] Publishing Release GUI (self-contained, win-x64)...
if exist "%PUBLISH_DIR%" (
    echo Removing stale GUI publish output...
    rmdir /s /q "%PUBLISH_DIR%"
    if exist "%PUBLISH_DIR%" (
        echo.
        echo BUILD FAILED: Could not remove stale GUI publish output.
        exit /b 1
    )
)
call dotnet publish "%CSPROJ%" -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo.
    echo BUILD FAILED!
    exit /b 1
)
if not exist "%PUBLISH_DIR%\RuApp.exe" (
    echo.
    echo BUILD FAILED: RuApp.exe was not created at:
    echo   "%PUBLISH_DIR%\RuApp.exe"
    exit /b 1
)
if not exist "%PUBLISH_DIR%\RuApp.runtimeconfig.json" (
    echo.
    echo BUILD FAILED: RuApp.runtimeconfig.json was not created.
    exit /b 1
)
findstr /C:"includedFrameworks" "%PUBLISH_DIR%\RuApp.runtimeconfig.json" >nul
if errorlevel 1 (
    echo.
    echo BUILD FAILED: GUI publish is not self-contained.
    echo Expected includedFrameworks in "%PUBLISH_DIR%\RuApp.runtimeconfig.json".
    exit /b 1
)

echo [11/12] Copying compiled XAML (.xbf) files...
if not exist "%PUBLISH_DIR%\Views" mkdir "%PUBLISH_DIR%\Views"
if not exist "%OBJ_DIR%\App.xbf" (
    echo BUILD FAILED: Compiled XAML App.xbf was not created.
    exit /b 1
)
if not exist "%OBJ_DIR%\MainWindow.xbf" (
    echo BUILD FAILED: Compiled XAML MainWindow.xbf was not created.
    exit /b 1
)
set "RU_XBF_FAIL=0"
for %%f in (App.xbf MainWindow.xbf) do (
    copy /Y "%OBJ_DIR%\%%f" "%PUBLISH_DIR%\"
    if errorlevel 1 set "RU_XBF_FAIL=1"
    if not exist "%PUBLISH_DIR%\%%f" set "RU_XBF_FAIL=1"
)
if "%RU_XBF_FAIL%"=="1" (
    echo BUILD FAILED: Could not publish compiled XAML.
    exit /b 1
)
if not exist "%OBJ_DIR%\Views\*.xbf" (
    echo BUILD FAILED: Compiled view XAML was not created.
    exit /b 1
)
set "RU_XBF_COUNT=0"
set "RU_XBF_FAIL=0"
for %%f in ("%OBJ_DIR%\Views\*.xbf") do (
    copy /Y "%%f" "%PUBLISH_DIR%\Views\"
    if errorlevel 1 set "RU_XBF_FAIL=1"
    if not exist "%PUBLISH_DIR%\Views\%%~nxf" set "RU_XBF_FAIL=1"
    set /a RU_XBF_COUNT+=1
)
if "%RU_XBF_FAIL%"=="1" (
    echo BUILD FAILED: Could not publish compiled view XAML.
    exit /b 1
)
echo Published !RU_XBF_COUNT! view XAML files.

call dotnet build-server shutdown >nul 2>&1

echo [12/12] Build complete!
echo.
echo ============================================
echo  Harness.Core:       %HARNESS_CORE_PROJECT%
echo  Harness.Persistence:%HARNESS_PERSISTENCE_PROJECT%
echo  Hosting:            %HOSTING_PROJECT%
echo  Client:             %CLIENT_PROJECT%
echo  CLI:                %CLI_PUBLISH_DIR%\RuAgent.Cli.exe
echo  Managed Worker:     %WORKER_PUBLISH_DIR%\RuAgent.Runtime.Worker.exe
echo  Runtime.Core:        %RUNTIME_CORE_PUBLISH_DLL%
echo  Broker:             %BROKER_PUBLISH_DIR%\RuAgent.Broker.exe
echo  Backend:            %BACKEND_PUBLISH_DIR%\RuBackend.exe
echo  Output:             %PUBLISH_DIR%\RuApp.exe
for %%I in ("%PUBLISH_DIR%\RuApp.exe") do echo  Published:          %%~tI
echo  Launch:             start_dev.bat
echo ============================================
echo.
