param([Parameter(Mandatory=$true)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
$staging = Split-Path -Parent ([IO.Path]::GetFullPath($RequestPath))
$resultPath = Join-Path (Split-Path -Parent $staging) 'last-uninstall.json'
try {
    if ((Get-Item -LiteralPath $RequestPath).Length -gt 4096) { throw 'uninstall_request_invalid' }
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    $executable = [IO.Path]::GetFullPath($request.Executable)
    if ([IO.Path]::GetFileName($executable) -notmatch '^unins[0-9]{3}\.exe$' -or $request.ParentPid -le 0) { throw 'uninstall_request_invalid' }
    $parent = Get-Process -Id $request.ParentPid -ErrorAction SilentlyContinue
    if (!$parent -or $parent.StartTime.ToUniversalTime().Ticks -ne $request.ParentStartTicks) { throw 'uninstall_parent_invalid' }
    Set-Content -LiteralPath (Join-Path $staging 'ready.txt') -Value ready -Encoding ascii
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!$parent.HasExited) {
        if (!(Test-Path -LiteralPath $RequestPath)) { throw 'uninstall_cancelled' }
        if ($timer.Elapsed.TotalSeconds -gt 30) { throw 'uninstall_parent_timeout' }
        Start-Sleep -Milliseconds 100
        $parent.Refresh()
    }
    if (!(Test-Path -LiteralPath $RequestPath)) { throw 'uninstall_cancelled' }
    $install = Split-Path -Parent $executable
    $registration = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OniAntou.Ru.CLI_is1'
    $registeredExe = [IO.Path]::GetFullPath(([string]$registration.UninstallString).Trim().Trim('"'))
    $registeredDirectory = [IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\','/')
    if (!$registeredExe.Equals($executable,[StringComparison]::OrdinalIgnoreCase) -or !$registeredDirectory.Equals($install,[StringComparison]::OrdinalIgnoreCase)) { throw 'uninstall_registration_invalid' }
    $ancestor = Get-Item -LiteralPath $executable
    while ($ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'uninstall_installation_invalid' }
        $ancestor = if ($ancestor.PSIsContainer) { $ancestor.Parent } else { $ancestor.Directory }
    }
    $markerPath = Join-Path $install 'ru-install.json'
    if ((Get-Item -LiteralPath $markerPath).Length -gt 4096 -or (Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json).Product -ne 'cli') { throw 'uninstall_installation_invalid' }
    # Never close another Ru window. Refuse while another client/host owns package files.
    foreach ($process in Get-Process -Name RuApp,RuAgent.Cli,RuBackend -ErrorAction SilentlyContinue) {
        if ($process.Path -and $process.Path.StartsWith($install + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'uninstall_busy: close other Ru windows and retry' }
    }
    # The native wizard is the explicit confirmation surface requested by ru uninstall.
    $uninstaller = Start-Process -FilePath $executable -WorkingDirectory $staging -WindowStyle Normal -PassThru -Wait -ArgumentList '/NORESTART'
    $status = if ($uninstaller.ExitCode -eq 0) { 'completed' } else { 'cancelled_or_failed' }
    @{status=$status;exitCode=$uninstaller.ExitCode;at=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
} catch {
    @{status='failed';error=$_.Exception.Message;at=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    exit 1
}
