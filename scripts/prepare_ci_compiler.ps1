# Install the pinned compiler only on a disposable GitHub Actions runner.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'Compiler setup requires a GitHub Actions runner.'
}
$setup = Join-Path $env:RUNNER_TEMP 'innosetup-6.5.3.exe'
$destination = Join-Path $env:RUNNER_TEMP 'ru-inno-6.5.3'
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_5_3/innosetup-6.5.3.exe' -OutFile $setup -TimeoutSec 60
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne '9345ee029faa0b7aed0818c3d5b227699ef9a496cce79e20c19eb9d6ef2e2c2d') {
    throw 'Inno Setup checksum mismatch.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne 'E0AB19C8D38CBF9C44709925122A7A02F8C70CB7') {
    throw 'Inno Setup publisher signature mismatch.'
}
$process = Start-Process -FilePath $setup -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $destination + '"'))
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $destination 'ISCC.exe'))) {
    throw 'Inno Setup compiler installation failed.'
}
