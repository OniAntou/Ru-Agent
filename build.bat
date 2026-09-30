@echo off
setlocal enabledelayedexpansion
echo ============================================
echo  Ru Desktop - Build + PRI Generation Script
echo  .NET 10 + ASP.NET Core + WinUI 3 (Unpackaged Self-Contained)
echo  Coding core: 41 HTTP, in-process model runner, CLI tracer
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

REM Publish output must start empty: dotnet publish leaves behind whatever a
REM previous build put there, and the flatten step would ship those orphans
REM (replaced dependencies, renamed adapters) into the distributable.
for %%D in ("%CLI_PUBLISH_DIR%" "%BROKER_PUBLISH_DIR%" "%BACKEND_PUBLISH_DIR%" "%PUBLISH_DIR%") do (
    if exist "%%~D" (
        rmdir /s /q "%%~D"
        if exist "%%~D" (
            echo.
            echo BUILD FAILED: Could not remove stale publish output:
            echo   "%%~D"
            exit /b 1
        )
    )
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

echo [4/12] Building RuAgent.Hosting (Broker Job Object + runtime descriptor)...
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

echo [7/12] Publishing RuAgentBroker (self-contained, win-x64)...
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

echo [8/12] Publishing ASP.NET Core backend (self-contained, win-x64)...
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

echo [9/12] Publishing Release GUI (self-contained, win-x64)...
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

echo [10/12] Copying compiled XAML (.xbf) files...
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

REM A control with its own markup compiles to Controls\*.xbf. Nothing published
REM that folder while every control was code-only, so the first control with XAML
REM would have loaded at runtime without its compiled markup and failed.
if exist "%OBJ_DIR%\Controls\*.xbf" (
    if not exist "%PUBLISH_DIR%\Controls" mkdir "%PUBLISH_DIR%\Controls"
    set "RU_XBF_FAIL=0"
    set /a RU_CTRL_XBF_COUNT=0
    for %%f in ("%OBJ_DIR%\Controls\*.xbf") do (
        copy /Y "%%f" "%PUBLISH_DIR%\Controls\"
        if errorlevel 1 set "RU_XBF_FAIL=1"
        if not exist "%PUBLISH_DIR%\Controls\%%~nxf" set "RU_XBF_FAIL=1"
        set /a RU_CTRL_XBF_COUNT+=1
    )
    if "!RU_XBF_FAIL!"=="1" (
        echo BUILD FAILED: Could not publish compiled control XAML.
        exit /b 1
    )
    echo Published !RU_CTRL_XBF_COUNT! control XAML files.
)

call dotnet build-server shutdown >nul 2>&1

REM A flat layout is what makes the GUI runnable outside this repository. The
REM launcher resolves its siblings first and the repository layout second, so
REM without this step the published GUI only worked from a checkout.
REM
REM Copy order matters. The GUI and CLI ship older builds of eight shared
REM Microsoft.Extensions assemblies than the two services do, so whichever tree
REM is copied last wins those files. The services go last because they reference
REM the newer versions; with the opposite order RuBackend.exe failed at startup
REM with "Could not load file or assembly
REM Microsoft.Extensions.Logging.Abstractions, Version=10.0.0.0".
echo [11/12] Flattening the runtime into one distributable directory...
set "RU_FLAT_FAIL=0"
REM The GUI is already at the destination, so only the three service trees are
REM copied. Including %PUBLISH_DIR% here made xcopy copy a directory into itself
REM and fail with "Cannot perform a cyclic copy".
for %%S in ("%CLI_PUBLISH_DIR%" "%BROKER_PUBLISH_DIR%" "%BACKEND_PUBLISH_DIR%") do (
    if not exist "%%~S" (
        echo BUILD FAILED: publish directory not found: %%~S
        set "RU_FLAT_FAIL=1"
    ) else (
        xcopy /E /I /Y /Q "%%~S\*" "%PUBLISH_DIR%\" >nul
        if errorlevel 1 (
            echo BUILD FAILED: could not copy %%~S into the flat layout.
            set "RU_FLAT_FAIL=1"
        )
    )
)
if "!RU_FLAT_FAIL!"=="1" exit /b 1
for %%E in (RuBackend.exe RuAgent.Broker.exe RuAgent.Cli.exe) do (
    if not exist "%PUBLISH_DIR%\%%E" (
        echo BUILD FAILED: %%E is missing from the flat layout at "%PUBLISH_DIR%".
        exit /b 1
    )
)

REM The GUI and CLI ship older builds of eight shared Microsoft.Extensions
REM assemblies than the two services do. A size difference between trees is
REM therefore expected rather than a defect: the flat layout holds the newer copy,
REM which the older consumers load happily, while the reverse order left
REM RuBackend.exe failing at startup with "Could not load file or assembly
REM Microsoft.Extensions.Logging.Abstractions, Version=10.0.0.0". The build only
REM fails when the flat layout is missing an assembly some tree needs.
set "RU_FLAT_MISMATCH=0"
for %%S in ("%CLI_PUBLISH_DIR%" "%BROKER_PUBLISH_DIR%" "%BACKEND_PUBLISH_DIR%") do (
    for %%F in ("%%~S\Microsoft.Extensions.*.dll") do (
        if not exist "%PUBLISH_DIR%\%%~nxF" (
            echo BUILD FAILED: flat layout is missing %%~nxF, which %%~S provides.
            set "RU_FLAT_MISMATCH=1"
        )
    )
)
if "!RU_FLAT_MISMATCH!"=="1" exit /b 1
echo Flattened: RuApp, RuBackend, Broker, CLI in one directory.

REM The GUI must carry the Windows App Runtime itself. Without it the publish
REM output holds only the bootstrap shim, which fails at startup on a machine with
REM no framework package installed, so the user would have to install one. These
REM files come from the framework payload and are absent in framework-dependent
REM mode, which makes them the check that the property is still in effect.
for %%R in (Microsoft.WindowsAppRuntime.dll Microsoft.UI.Xaml.dll DWriteCore.dll MRM.dll) do (
    if not exist "%PUBLISH_DIR%\%%R" (
        echo BUILD FAILED: %%R is missing, so the GUI would need the Windows App Runtime installed.
        echo   WindowsAppSDKSelfContained must stay true in gui-winui3\RuApp.csproj.
        exit /b 1
    )
)
echo Windows App Runtime bundled: the app installs nothing.

echo [12/12] Build complete!
echo.
echo ============================================
echo  Harness.Core:       %HARNESS_CORE_PROJECT%
echo  Harness.Persistence:%HARNESS_PERSISTENCE_PROJECT%
echo  Hosting:            %HOSTING_PROJECT%
echo  Client:             %CLIENT_PROJECT%
echo  CLI:                %CLI_PUBLISH_DIR%\RuAgent.Cli.exe
echo  Broker:             %BROKER_PUBLISH_DIR%\RuAgent.Broker.exe
echo  Backend:            %BACKEND_PUBLISH_DIR%\RuBackend.exe
echo  Output:             %PUBLISH_DIR%\RuApp.exe
echo  Distributable:      %PUBLISH_DIR% (all four executables side by side)
echo  Self-contained:     .NET runtime and Windows App Runtime both included
for %%I in ("%PUBLISH_DIR%\RuApp.exe") do echo  Published:          %%~tI
echo  Launch:             start_dev.bat
echo ============================================
echo.
