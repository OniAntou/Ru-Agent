@echo off
if "%~1"=="" (
  "D:\LLM\backend-dotnet\RuAgent.Cli\bin\Release\net10.0\win-x64\publish\RuAgent.Cli.exe" agent chat --workspace "%CD%"
) else (
  "D:\LLM\backend-dotnet\RuAgent.Cli\bin\Release\net10.0\win-x64\publish\RuAgent.Cli.exe" %*
)
