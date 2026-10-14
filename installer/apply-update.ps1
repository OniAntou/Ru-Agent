[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = $null
$resultPath = $null
$updateLock = $null
$lockHeld = $false
try {
    $requestFile = [IO.Path]::GetFullPath($RequestPath)
    if ((Get-Item -LiteralPath $requestFile).Length -gt 16384) { throw 'update_request_invalid' }
    $request = Get-Content -LiteralPath $requestFile -Raw | ConvertFrom-Json
    if ($request.Protocol -ne 1 -or $request.Product -notin @('desktop','cli') -or $request.Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'update_request_invalid' }
    $installer = [IO.Path]::GetFullPath($request.InstallerPath)
    # AppContext.BaseDirectory ends in a separator. Remove it before placing
    # the path in the quoted /DIR argument used by Windows command-line parsers.
    $installDirectory = [IO.Path]::GetFullPath($request.InstallDirectory).TrimEnd('\','/')
    if ([IO.Path]::GetDirectoryName($installer) -ne [IO.Path]::GetDirectoryName($requestFile)) { throw 'update_request_invalid' }
    $updateRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($requestFile))
    $expectedResult = Join-Path $updateRoot 'last-update.json'
    if ([IO.Path]::GetFullPath($request.ResultPath) -ne $expectedResult -or $request.ParentPid -le 0 -or $request.ParentStartTicks -le 0) { throw 'update_request_invalid' }
    $resultPath = $expectedResult
    $readyPath = [IO.Path]::GetFullPath($request.ReadyPath)
    if ([IO.Path]::GetDirectoryName($readyPath) -ne [IO.Path]::GetDirectoryName($requestFile) -or [IO.Path]::GetFileName($readyPath) -notmatch '^worker-ready-[0-9a-f]{32}\.txt$') { throw 'update_request_invalid' }
    if ($request.Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') { throw 'update_request_invalid' }
    $productName = if ($request.Product -eq 'desktop') { 'Desktop' } else { 'CLI' }
    if ([IO.Path]::GetFileName($installer) -ne ('Ru-' + $productName + '-' + $request.Version + '-win-x64-Setup.exe')) { throw 'update_request_invalid' }
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $updateLock = New-Object Threading.Mutex($false, ('Global\Ru-update-' + $userSid))
    try { $lockHeld = $updateLock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $lockHeld = $true }
    if (-not $lockHeld) { throw 'update_already_in_progress' }
    $markerPath = Join-Path $installDirectory 'ru-install.json'
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.Product -ne $request.Product -or $marker.Architecture -ne 'win-x64') { throw 'update_installation_invalid' }
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $request.Sha256) { throw 'update_digest_mismatch' }
    Set-Content -LiteralPath $readyPath -Value 'ready' -Encoding ascii

    # Wait for the exact caller to leave. PID reuse never selects another process.
    $parent = Get-Process -Id $request.ParentPid -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $request.ParentStartTicks) {
        if (-not $parent.WaitForExit(60000)) { throw 'update_parent_still_running' }
    }
    # Do not kill windows, detached runs or locked-file owners. Wait briefly for
    # the idle host shutdown; the installer also checks locks before copying.
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $locked = $false
        if ((Get-Item -LiteralPath $installDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'update_installation_invalid' }
        if (Get-ChildItem -LiteralPath $installDirectory -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw 'update_installation_invalid' }
        foreach ($file in Get-ChildItem -LiteralPath $installDirectory -Recurse -File) {
            try {
                $handle = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $handle.Dispose()
            } catch [IO.IOException] { $locked = $true; break }
        }
        if (-not $locked) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($locked) { throw 'update_files_in_use' }
    if (-not (Test-Path -LiteralPath $requestFile)) { throw 'update_cancelled' }
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS',('/DIR="' + $installDirectory + '"'),('/LOG="' + (Join-Path ([IO.Path]::GetDirectoryName($requestFile)) 'install.log') + '"'))
    # Pin the verified bytes against writes/deletion until Setup has finished.
    $verifiedInstaller = [IO.File]::Open($installer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($verifiedInstaller)).Replace('-','') } finally { $sha.Dispose() }
        if ($digest -ne $request.Sha256) { throw 'update_digest_mismatch' }
        $setup = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    } finally { $verifiedInstaller.Dispose() }
    if ($setup.ExitCode -ne 0) { throw ('update_installer_failed_' + $setup.ExitCode) }
    $updated = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($updated.Version -ne $request.Version -or $updated.Product -ne $request.Product) { throw 'update_version_mismatch' }
    @{ status='installed'; version=$request.Version; product=$request.Product; at=[DateTime]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    if ($request.RestartDesktop -and $request.Product -eq 'desktop') {
        # Visible restart was explicitly requested by the GUI's Install button.
        Start-Process -FilePath (Join-Path $installDirectory 'RuApp.exe') -WorkingDirectory $installDirectory
    }
} catch {
    if ($resultPath) {
        @{ status='failed'; error=$_.Exception.Message; at=[DateTime]::UtcNow.ToString('O') } |
            ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    }
    exit 1
} finally {
    if ($lockHeld) { $updateLock.ReleaseMutex() }
    if ($updateLock) { $updateLock.Dispose() }
}
