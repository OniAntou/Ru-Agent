# This intentionally changes installation registration and user PATH. Run only
# on a disposable GitHub Actions machine, never as a local developer test.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or $env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) { throw 'Installer smoke requires a disposable GitHub Actions runner.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repoRoot ('artifacts\releases\' + $Version)
$work = Join-Path $env:RUNNER_TEMP ('ru-installer-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'Ru'
$sentinel = Join-Path $profile 'distribution-smoke.keep'
if (Test-Path -LiteralPath $sentinel) { throw 'Unexpected existing smoke profile.' }
New-Item -ItemType Directory -Path $profile -Force | Out-Null
Set-Content -LiteralPath $sentinel 'synthetic session and credential preservation fixture'
$sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
$previousPath = [Environment]::GetEnvironmentVariable('Path','User')

function Invoke-Setup([string]$File,[string]$Destination) {
    $process = Start-Process -FilePath $File -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS',('/DIR="' + $Destination + '"'))
    if ($process.ExitCode -ne 0) { throw "Setup failed: $($process.ExitCode)" }
}
foreach ($product in @('CLI','Desktop')) {
    $destination = Join-Path $work $product
    $candidate = Join-Path $releaseRoot "Ru-$product-$Version-win-x64-Setup.exe"
    Invoke-Setup $candidate $destination
    $marker = Get-Content -LiteralPath (Join-Path $destination 'ru-install.json') -Raw | ConvertFrom-Json
    if ($marker.Version -ne $Version -or $marker.Product -ne $product.ToLowerInvariant()) { throw 'Installed marker mismatch.' }
    if ($product -eq 'CLI') {
        $installedVersion = & (Join-Path $destination 'RuAgent.Cli.exe') --version
        if ($LASTEXITCODE -ne 0 -or $installedVersion -ne $Version) { throw 'Installed CLI version mismatch.' }
        $userPath = [Environment]::GetEnvironmentVariable('Path','User')
        if (($userPath -split ';') -notcontains $destination) { throw 'CLI PATH entry missing.' }
        # Replace the marker only to exercise installer upgrade ownership; the
        # old staged DLLs are this candidate, not evidence for binary compatibility.
        $marker.Version = '0.0.0-preview.0'
        $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'ru-install.json') -Encoding utf8
        $obsolete = Join-Path $destination 'obsolete-owned.fixture'
        Set-Content -LiteralPath $obsolete 'obsolete owned payload'
        Add-Content -LiteralPath (Join-Path $destination 'ru-files.lst') 'obsolete-owned.fixture'
        $unowned = Join-Path $destination 'local-unowned.fixture'
        Set-Content -LiteralPath $unowned 'unowned local file'
        $staging = Join-Path $work ('updates\' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        $stagedInstaller = Join-Path $staging (Split-Path -Leaf $candidate)
        Copy-Item -LiteralPath $candidate -Destination $stagedInstaller
        $stagedHelper = Join-Path $staging 'apply-update.ps1'
        Copy-Item -LiteralPath (Join-Path $destination 'apply-update.ps1') -Destination $stagedHelper
        $caller = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 2')
        $result = Join-Path (Split-Path -Parent $staging) 'last-update.json'
        $request = Join-Path $staging 'update-request.json'
        @{ Protocol=1; Product='cli'; Version=$Version; Sha256=(Get-FileHash -LiteralPath $stagedInstaller).Hash; InstallerPath=$stagedInstaller; InstallDirectory=$destination; ParentPid=$caller.Id; ParentStartTicks=$caller.StartTime.ToUniversalTime().Ticks; RestartDesktop=$false; ResultPath=$result; ReadyPath=(Join-Path $staging ('worker-ready-' + [guid]::NewGuid().ToString('N') + '.txt')) } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $stagedHelper -RequestPath $request
        if ($LASTEXITCODE -ne 0 -or (Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).status -ne 'installed') { throw 'Maintenance handoff failed.' }
        if (Test-Path -LiteralPath $obsolete) { throw 'Obsolete owned file survived upgrade.' }
        if (-not (Test-Path -LiteralPath $unowned)) { throw 'Upgrade deleted an unowned file.' }
    }
    if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Install/update changed profile data.' }
    $uninstall = Start-Process -FilePath (Join-Path $destination 'unins000.exe') -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    if ($uninstall.ExitCode -ne 0 -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Uninstall failed or changed profile data.' }
    if ($product -eq 'CLI' -and [Environment]::GetEnvironmentVariable('Path','User') -ne $previousPath) { throw 'Uninstall did not restore the original user PATH.' }
}
Write-Host 'Installer install/update/uninstall checks passed on the disposable runner.'
