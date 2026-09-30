@echo off
setlocal
echo Stopping Ru Desktop and backend processes...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$names = @('RuApp', 'RuBackend', 'RuAgent.Broker', 'RuAgent.Cli');" ^
    "$root = '%~dp0'.TrimEnd('\') + '\';" ^
    "$mine = { param($p) $p.Path -and $p.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) };" ^
    "$targets = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName -and (& $mine $_) });" ^
    "if ($targets.Count -eq 0) { Write-Host 'No Ru processes from this checkout are running.' } else {" ^
    "  foreach ($p in $targets) { Write-Host ('Closing ' + $p.ProcessName + ' (pid ' + $p.Id + ')'); $null = $p.CloseMainWindow() };" ^
    "  foreach ($p in $targets) { if (-not $p.WaitForExit(5000)) { Write-Host ('Force killing ' + $p.ProcessName + ' (pid ' + $p.Id + ')'); Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } } };" ^
    "Write-Host 'Stopping .NET build server nodes...';" ^
    "& dotnet build-server shutdown;" ^
    "$buildServerExit = $LASTEXITCODE;" ^
    "$left = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName -and (& $mine $_) });" ^
    "if ($left.Count -gt 0) { Write-Host ('ERROR: still running: ' + (($left | ForEach-Object { $_.ProcessName + ' (pid ' + $_.Id + ')' }) -join ', ')); exit 1 };" ^
    "if ($buildServerExit -ne 0) { Write-Host ('ERROR: dotnet build-server shutdown exited with ' + $buildServerExit); exit 1 };" ^
    "Write-Host 'All Ru processes and build servers stopped.'"
if errorlevel 1 (
    endlocal
    exit /b 1
)

endlocal
exit /b 0
