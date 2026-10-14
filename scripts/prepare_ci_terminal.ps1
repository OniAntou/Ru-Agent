# Tool preparation for disposable GitHub Actions runners only.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'Terminal prerequisite setup requires a GitHub Actions runner.'
}
$archive = Join-Path $env:RUNNER_TEMP 'ripgrep-14.1.1.zip'
$destination = Join-Path $env:RUNNER_TEMP 'ru-terminal-tools'
Invoke-WebRequest -Uri 'https://github.com/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-pc-windows-msvc.zip' -OutFile $archive -TimeoutSec 60
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'd0f534024c42afd6cb4d38907c25cd2b249b79bbe6cc1dbee8e3e37c2b6e25a1') {
    throw 'Ripgrep archive checksum mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $destination
$toolDirectory = Join-Path $destination 'ripgrep-14.1.1-x86_64-pc-windows-msvc'
& (Join-Path $toolDirectory 'rg.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Ripgrep startup failed.' }
$toolDirectory | Out-File -LiteralPath $env:GITHUB_PATH -Encoding utf8 -Append
