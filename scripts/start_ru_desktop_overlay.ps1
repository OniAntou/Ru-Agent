[CmdletBinding()]
param(
    [switch]$ClickThrough,
    [string]$Sender = 'VTubeStudioSpout'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$overlayRoot = Join-Path $PSScriptRoot '..\tools\ru_desktop_overlay'
$exe = Join-Path $overlayRoot 'out\build\Release\RuDesktopOverlay.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'RuDesktopOverlay.exe is missing. Run scripts\build_ru_desktop_overlay.ps1 first.'
}

$arguments = @('--sender', $Sender)
if ($ClickThrough) {
    $arguments += '--click-through'
}

Start-Process -FilePath $exe -ArgumentList $arguments
