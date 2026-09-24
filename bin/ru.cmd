@echo off
if "%~1"=="" (
  "%~dp0..\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuAgent.Cli.exe" agent chat --workspace "%CD%"
) else (
  "%~dp0..\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuAgent.Cli.exe" %*
)
