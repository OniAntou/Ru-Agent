[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')][string]$Version,
    [Parameter(Mandatory)][string]$IsccPath,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if ($Version.Length -gt 128) { throw 'Invalid release version.' }
$prerelease = $Version.Split('-',2)[1]
foreach ($identifier in ($prerelease -split '\.')) {
    if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$releaseRoot = Join-Path $repoRoot ('artifacts\releases\' + $Version)
if (Test-Path -LiteralPath $releaseRoot) { throw 'Release output already exists. Choose a new version or move the previous output aside.' }
$compiler = (Resolve-Path -LiteralPath $IsccPath).Path
$buildLock = [Threading.Mutex]::new($false, 'Global\ru-test-matrix-run')
$held = $false
$previousVersion = $env:RuReleaseVersion
$previousSplit = $env:RU_RELEASE_SPLIT
try {
    try { $held = $buildLock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'Another Ru build/test matrix is running.' }
    $env:RuReleaseVersion = $Version
    $env:RU_RELEASE_SPLIT = '1'
    if (-not $SkipBuild) {
        Push-Location $repoRoot
        try { & .\build.bat; if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' } } finally { Pop-Location }
    }
    $gui = Join-Path $repoRoot 'gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish'
    $cli = Join-Path $repoRoot 'backend-dotnet\RuAgent.Cli\bin\Release\net10.0-windows\win-x64\publish'
    $backend = Join-Path $repoRoot 'backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish'
    foreach ($publishRoot in @($gui,$cli,$backend)) {
        $resolved = [IO.Path]::GetFullPath($publishRoot)
        if (-not $resolved.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $resolved.EndsWith('\publish')) { throw 'Invalid publish boundary.' }
        if (-not (Test-Path -LiteralPath $resolved)) { throw 'Publish output missing.' }
        $assembly = Join-Path $resolved 'RuAgent.Updates.dll'
        if ((Get-Item -LiteralPath $assembly).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw 'Publish version does not match the requested release.' }
    }
    New-Item -ItemType Directory -Path $releaseRoot | Out-Null
    foreach ($product in @('Desktop','CLI')) {
        $package = Join-Path $releaseRoot $product
        New-Item -ItemType Directory -Path $package | Out-Null
        $source = if ($product -eq 'Desktop') { $gui } else { $cli }
        Copy-Item -Path (Join-Path $source '*') -Destination $package -Recurse
        if ($product -eq 'CLI') {
            # Host dependencies are copied last: its newer Microsoft.Extensions
            # assemblies must win, just as in build.bat's desktop layout.
            Copy-Item -Path (Join-Path $backend '*') -Destination $package -Recurse -Force
            Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\ru.cmd') -Destination $package
        }
        $unexpected = if ($product -eq 'Desktop') { 'RuAgent.Cli.exe' } else { 'RuApp.exe' }
        if (Test-Path -LiteralPath (Join-Path $package $unexpected)) { throw 'Separate product package contains the other frontend.' }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\apply-update.ps1') -Destination $package
        Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $package
        Copy-Item -LiteralPath (Join-Path $repoRoot 'gui-winui3\Assets\AppIcon.ico') -Destination (Join-Path $package 'Ru.ico')
        @{ Product=$product.ToLowerInvariant(); Version=$Version; Architecture='win-x64'; UpdateProtocol=1 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'ru-install.json') -Encoding utf8
        # The compiler embeds exactly this tree; no keys, settings or sessions.
        Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object { $_.FullName.Substring($package.Length+1) } | Set-Content -LiteralPath (Join-Path $package 'ru-files.lst') -Encoding utf8
        & $compiler ('/DProduct=' + $product) ('/DReleaseVersion=' + $Version) ('/DPackageRoot=' + $package) ('/DOutputRoot=' + $releaseRoot) (Join-Path $repoRoot 'installer\Ru.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed for $product." }
        Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $releaseRoot "Ru-$product-$Version-win-x64.zip") -CompressionLevel Optimal
    }
    Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Extension -in @('.exe','.zip') | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name } | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release artifacts: $releaseRoot"
} finally {
    $env:RuReleaseVersion = $previousVersion
    $env:RU_RELEASE_SPLIT = $previousSplit
    if ($held) { $buildLock.ReleaseMutex() }
    $buildLock.Dispose()
}
