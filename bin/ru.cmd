@echo off
setlocal
set "RU_CLI=%~dp0..\backend-dotnet\RuAgent.Cli\bin\Release\net10.0-windows\win-x64\publish\RuAgent.Cli.exe"
if not exist "%RU_CLI%" set "RU_CLI=%~dp0..\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuAgent.Cli.exe"
if "%~1"=="" (
  "%RU_CLI%" agent chat --workspace "%CD%"
) else (
  "%RU_CLI%" %*
)
