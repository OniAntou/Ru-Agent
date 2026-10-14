# Workspace-only native acceptance: dedicated test AppId, no uninstall
# registration, shortcuts, user PATH changes or application launch. Production
# registration/PATH behavior belongs to installer_smoke.ps1 on disposable CI.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z')][string]$Version,
    [Parameter(Mandatory)][string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$work = Join-Path $repoRoot ('artifacts\release-verification\native-' + [guid]::NewGuid().ToString('N'))
if (-not [IO.Path]::GetFullPath($work).StartsWith((Join-Path $repoRoot 'artifacts\release-verification\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture boundary.' }
New-Item -ItemType Directory -Path $work | Out-Null
$previousPath = [Environment]::GetEnvironmentVariable('Path','User')
$installedProducts = @()
foreach ($product in @('CLI','Desktop')) {
    $package = Join-Path $repoRoot "artifacts\releases\$Version\$product"
    $output = Join-Path $work ($product + '-setup')
    New-Item -ItemType Directory -Path $output | Out-Null
    & $IsccPath /DLocalSmoke=1 ('/DProduct=' + $product) ('/DReleaseVersion=' + $Version) ('/DPackageRoot=' + $package) ('/DOutputRoot=' + $output) (Join-Path $repoRoot 'installer\Ru.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Local test installer compilation failed.' }
    $installer = Join-Path $output "Ru-$product-$Version-win-x64-Setup.exe"
    $destination = Join-Path $work ($product + '-installed fixture')
    $setupArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS',('/DIR="' + $destination + '"'))
    $setup = Start-Process -FilePath $installer -ArgumentList $setupArguments -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw ('Local install failed: ' + $setup.ExitCode) }
    $markerPath = Join-Path $destination 'ru-install.json'
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.Version -ne $Version) { throw 'Local install version mismatch.' }
    if ($product -eq 'CLI') {
        $actualVersion = & (Join-Path $destination 'RuAgent.Cli.exe') --version
        if ($actualVersion -ne $Version -or $LASTEXITCODE -ne 0) { throw 'Installed CLI did not boot its version command.' }
    }
    $marker.Version = '0.0.0-preview.0'
    $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding utf8
    $obsolete = Join-Path $destination 'obsolete-owned.fixture'
    Set-Content -LiteralPath $obsolete 'obsolete file'
    Add-Content -LiteralPath (Join-Path $destination 'ru-files.lst') 'obsolete-owned.fixture'
    $unowned = Join-Path $destination 'unowned.fixture'
    Set-Content -LiteralPath $unowned 'keep this file'
    $lock = [IO.File]::Open((Join-Path $destination 'RuAgent.Updates.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try {
        $blocked = Start-Process -FilePath $installer -ArgumentList $setupArguments -WindowStyle Hidden -Wait -PassThru
        if ($blocked.ExitCode -eq 0 -or (Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json).Version -ne '0.0.0-preview.0') { throw 'Locked installation was not refused before replacement.' }
    } finally { $lock.Dispose() }
    $staging = Join-Path $work ('updates\' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $stagedInstaller = Join-Path $staging (Split-Path -Leaf $installer)
    Copy-Item -LiteralPath $installer -Destination $stagedInstaller
    $helper = Join-Path $staging 'apply-update.ps1'
    Copy-Item -LiteralPath (Join-Path $destination 'apply-update.ps1') -Destination $helper
    $caller = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 2')
    $resultPath = Join-Path (Split-Path -Parent $staging) 'last-update.json'
    $requestPath = Join-Path $staging 'update-request.json'
    @{ Protocol=1; Product=$product.ToLowerInvariant(); Version=$Version; Sha256=(Get-FileHash -LiteralPath $stagedInstaller).Hash; InstallerPath=$stagedInstaller; InstallDirectory=($destination + '\'); ParentPid=$caller.Id; ParentStartTicks=$caller.StartTime.ToUniversalTime().Ticks; RestartDesktop=$false; ResultPath=$resultPath; ReadyPath=(Join-Path $staging ('worker-ready-' + [guid]::NewGuid().ToString('N') + '.txt')) } | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper -RequestPath $requestPath
    if ($LASTEXITCODE -ne 0 -or (Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json).status -ne 'installed') { throw 'Native update helper failed.' }
    if (Test-Path -LiteralPath $obsolete) { throw 'Obsolete owned payload was retained.' }
    if (-not (Test-Path -LiteralPath $unowned)) { throw 'Unowned payload was deleted.' }
    $uninstall = Start-Process -FilePath (Join-Path $destination 'unins000.exe') -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $markerPath) -or -not (Test-Path -LiteralPath $unowned)) { throw 'Local uninstall failed ownership checks.' }
    $installedProducts += $product
}
if ([Environment]::GetEnvironmentVariable('Path','User') -ne $previousPath) { throw 'Workspace smoke changed user PATH.' }
@{ status='passed'; version=$Version; products=$installedProducts; scope='workspace-only; registration and PATH require CI'; checks=@('install','locked-files deny','helper update','obsolete-owned removal','unowned preservation','uninstall') } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'acceptance.json') -Encoding utf8
Write-Host "Workspace-only installer acceptance passed: $work"
