# Windows PowerShell 5.1+. Download from the official repository and inspect
# this file before running it. No administrator or .NET SDK is required.
[CmdletBinding()]
param([ValidateSet('Stable','Preview')][string]$Channel = 'Stable')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Parse-ReleaseVersion([string]$Value) {
    if ($Value.Length -gt 128 -or $Value -notmatch '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z') { return $null }
    $parts = @($Matches[1],$Matches[2],$Matches[3]); $pre = $Matches[4]
    foreach ($part in $parts) { $number = 0; if (-not [int]::TryParse($part,[ref]$number)) { return $null } }
    foreach ($part in ($pre -split '\.')) { if ($part -match '^0[0-9]+$') { return $null } }
    return @{ Major=[int]$parts[0]; Minor=[int]$parts[1]; Patch=[int]$parts[2]; Pre=$pre; Text=$Value.TrimStart('v') }
}
function Compare-ReleaseVersion($Left,$Right) {
    foreach ($part in @('Major','Minor','Patch')) { if ($Left[$part] -gt $Right[$part]) { return 1 }; if ($Left[$part] -lt $Right[$part]) { return -1 } }
    if (-not $Left.Pre) { if ($Right.Pre) { return 1 }; return 0 }
    if (-not $Right.Pre) { return -1 }
    $a = $Left.Pre -split '\.'; $b = $Right.Pre -split '\.'
    for ($i=0; $i -lt [Math]::Min($a.Length,$b.Length); $i++) {
        $an = $a[$i] -match '^[0-9]+$'; $bn = $b[$i] -match '^[0-9]+$'
        if ($an -and $bn) { $difference = $a[$i].Length.CompareTo($b[$i].Length); if ($difference -eq 0) { $difference = [string]::CompareOrdinal($a[$i],$b[$i]) } }
        elseif ($an -ne $bn) { $difference = if ($an) { -1 } else { 1 } }
        else { $difference = [string]::CompareOrdinal($a[$i],$b[$i]) }
        if ($difference -ne 0) { return $difference }
    }
    return $a.Length.CompareTo($b.Length)
}
function Test-DownloadOrigin([Uri]$Uri) {
    return $Uri.IsAbsoluteUri -and $Uri.Scheme -eq 'https' -and $Uri.Port -eq 443 -and -not $Uri.UserInfo -and (
        ($Uri.Host -eq 'github.com' -and $Uri.AbsolutePath.StartsWith('/OniAntou/Ru-Agent/releases/download/')) -or
        $Uri.Host -in @('release-assets.githubusercontent.com','objects.githubusercontent.com'))
}

$handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
$http = [Net.Http.HttpClient]::new($handler); $http.Timeout = [TimeSpan]::FromMinutes(10)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('Ru-Install/1.0')
$deadline = [Threading.CancellationTokenSource]::new()
$deadline.CancelAfter([TimeSpan]::FromSeconds(30))
$staging = Join-Path $env:TEMP ('ru-install-' + [guid]::NewGuid().ToString('N'))
$stream = $null; $output = $null; $response = $null
try {
    $response = $http.GetAsync('https://api.github.com/repos/OniAntou/Ru-Agent/releases?per_page=100',[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw 'release_check_failed' }
    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $memory = [IO.MemoryStream]::new(); $buffer = New-Object byte[] 8192
    try {
        while (($count=$stream.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) { if ($memory.Length+$count -gt 2097152) { throw 'release_metadata_too_large' }; $memory.Write($buffer,0,$count) }
        $releases = [Text.Encoding]::UTF8.GetString($memory.ToArray()) | ConvertFrom-Json
    } finally { $memory.Dispose(); $stream.Dispose(); $stream=$null; $response.Dispose(); $response=$null }
    $selected=$null; $selectedVersion=$null
    foreach ($release in $releases) {
        if ($release.draft -ne $false) { continue }
        $version = Parse-ReleaseVersion $release.tag_name
        if (-not $version -or ($Channel -eq 'Stable' -and ($release.prerelease -or $version.Pre))) { continue }
        if ($selectedVersion -and (Compare-ReleaseVersion $version $selectedVersion) -le 0) { continue }
        $name = 'Ru-CLI-' + $version.Text + '-win-x64-Setup.exe'
        $assets = @($release.assets | Where-Object name -eq $name)
        if ($assets.Count -ne 1) { continue }
        $asset=$assets[0]
        if ($asset.digest -notmatch '^sha256:[0-9a-fA-F]{64}\z' -or $asset.size -le 0 -or $asset.size -gt 1073741824) { throw 'release_asset_invalid' }
        if ($asset.browser_download_url -cne ('https://github.com/OniAntou/Ru-Agent/releases/download/' + $release.tag_name + '/' + $name)) { throw 'release_origin_invalid' }
        $selected=$asset; $selectedVersion=$version
    }
    if (-not $selected) { throw "No CLI release is available on $Channel yet." }
    $deadline.CancelAfter([TimeSpan]::FromMinutes(10))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $path = Join-Path $staging $selected.name
    $uri = [Uri]$selected.browser_download_url
    for ($redirect=0; $redirect -le 4; $redirect++) {
        if (-not (Test-DownloadOrigin $uri)) { throw 'release_origin_invalid' }
        $response = $http.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
            $location=$response.Headers.Location; $response.Dispose(); $response=$null
            if (-not $location) { throw 'download_redirect_invalid' }
            $uri=[Uri]::new($uri,$location); continue
        }
        break
    }
    if (-not $response -or -not $response.IsSuccessStatusCode) { throw 'download_failed' }
    $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $output=[IO.File]::Open($path,[IO.FileMode]::CreateNew)
    $buffer=New-Object byte[] 81920; $written=0L
    while (($count=$stream.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) { $written+=$count; if ($written -gt $selected.size) { throw 'download_size_mismatch' }; $output.Write($buffer,0,$count) }
    $output.Dispose(); $output=$null
    if ($written -ne $selected.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $selected.digest.Substring(7)) { throw 'download_verification_failed' }
    Write-Host "Verified Ru CLI $($selectedVersion.Text). Installing for the current user."
    $setup = Start-Process -FilePath $path -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS')
    if ($setup.ExitCode -ne 0) { throw ('installation_failed_' + $setup.ExitCode) }
    $preferences = Join-Path $env:LOCALAPPDATA 'Ru\updates'
    New-Item -ItemType Directory -Path $preferences -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $preferences 'channel.txt') -Value $Channel.ToLowerInvariant() -Encoding ascii
    Write-Host 'Installed. Open a new terminal and run ru. Future updates: ru update.'
} finally {
    if ($output) { $output.Dispose() }; if ($stream) { $stream.Dispose() }; if ($response) { $response.Dispose() }; $http.Dispose(); $deadline.Dispose()
    # Delete only the private, direct temporary child created by this invocation.
    if ((Split-Path -Parent ([IO.Path]::GetFullPath($staging))) -eq [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
