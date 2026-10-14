@echo off
if "%~1"=="" (
  "%~dp0RuAgent.Cli.exe" agent chat --workspace "%CD%"
) else (
  "%~dp0RuAgent.Cli.exe" %*
)
