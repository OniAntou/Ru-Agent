@echo off
setlocal
echo Stopping Ru Desktop and backend processes...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$names = @('RuApp', 'RuBackend', 'RuAgent.Runtime.Worker', 'RuAgent.Broker', 'RuAgent.Cli');" ^
    "Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName } | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue };" ^
    "Write-Host 'Stopping .NET build server nodes...';" ^
    "& dotnet build-server shutdown;" ^
    "Write-Host 'All Ru processes and build servers stopped.'"

endlocal
exit /b 0
